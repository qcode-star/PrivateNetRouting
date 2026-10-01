using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Runtime;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>
/// Локальный CONNECT-прокси (порт ts-fwd). Имена резолвит DoH через SOCKS5-апстрим —
/// системный резолв не используется (он перекрыт банами), дозвон идёт по IP тем же SOCKS5.
/// Биндится на основной адрес из конфига и один раз на шлюзе WSL NAT, когда адаптер появится.
/// Штатный режим — тишина в логе: старт/стоп, варнинги, ошибки.
/// </summary>
public sealed class ForwardProxyService : IAsyncDisposable
{
    private static readonly byte[] EstablishResponse =
        "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();

    private static readonly byte[] BadRequestResponse =
        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private static readonly byte[] BadGatewayResponse =
        "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private readonly ForwardProxyConfig _config;
    private readonly IDnsResolver _resolver;
    private readonly Socks5Client _socks;
    private readonly ActivityMarker _marker;
    private readonly FileLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly object _gate = new();
    private Task? _wslBindTask;
    private int _activeConnections;
    private long _totalConnections;
    private long _failedConnections;
    private volatile bool _running;

    public ForwardProxyService(
        ForwardProxyConfig config,
        IDnsResolver resolver,
        Socks5Client socks,
        ActivityMarker marker,
        FileLog log)
    {
        _config = config;
        _resolver = resolver;
        _socks = socks;
        _marker = marker;
        _log = log;
    }

    public bool IsRunning => _running;

    public IReadOnlyList<string> ListenerEndpoints
    {
        get
        {
            lock (_gate)
            {
                return _listeners
                    .Where(l => l.LocalEndpoint is IPEndPoint)
                    .Select(l => ((IPEndPoint)l.LocalEndpoint!).ToString())
                    .ToList();
            }
        }
    }

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public long TotalConnections => Volatile.Read(ref _totalConnections);

    public long FailedConnections => Volatile.Read(ref _failedConnections);

    public async Task StartAsync(CancellationToken ct)
    {
        if (_running)
        {
            return;
        }

        var listen = Socks5Client.ParseEndPoint(_config.Listen);
        var listener = BindListener(listen);
        _ = AcceptLoopAsync(listener);
        _log.Info($"forward-proxy: слушаю {listen}");

        if (_config.BindWslGateway)
        {
            _wslBindTask = Task.Run(() => WslBindWaitAsync());
        }

        _running = true;
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        await _cts.CancelAsync();
        lock (_gate)
        {
            foreach (var listener in _listeners)
            {
                listener.Stop();
            }

            _listeners.Clear();
        }

        _log.Info("forward-proxy: остановлен");
    }

    /// <summary>Одноразовый бинд на шлюзе WSL: ждём адаптер с повторами, биндим, выходим.</summary>
    private async Task WslBindWaitAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var gateway = WslGateway.FindIpv4();
            if (gateway is not null)
            {
                try
                {
                    var listener = BindListener(new IPEndPoint(gateway, ((IPEndPoint)Socks5Client.ParseEndPoint(_config.Listen)).Port));
                    _ = AcceptLoopAsync(listener);
                    _log.Info($"forward-proxy: слушаю WSL-шлюз {gateway} (одноразовый бинд)");
                }
                catch (SocketException ex)
                {
                    _log.Error($"forward-proxy: бинд на WSL-шлюзе {gateway} не удался: {ex.Message}");
                }

                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TcpListener BindListener(IPEndPoint endpoint)
    {
        var listener = new TcpListener(endpoint);
        listener.Start(backlog: 128);
        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return listener;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // слушатель снят остановкой службы
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
        try
        {
            var stream = client.GetStream();

            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            headerCts.CancelAfter(TimeSpan.FromSeconds(_config.HeaderTimeoutSeconds));
            var header = await ReadHeaderAsync(stream, headerCts.Token);
            if (header is null)
            {
                return;
            }

            if (!ConnectRequestParser.TryParse(header, out var request, out var parseError))
            {
                await stream.WriteAsync(BadRequestResponse, _cts.Token);
                _log.Warn($"forward-proxy: {client.Client.RemoteEndPoint}: {parseError}");
                return;
            }

            var target = request!;
            var destination = IPAddress.TryParse(target.Host, out var literal)
                ? new ResolveResult(literal, "literal", null)
                : await _resolver.ResolveAsync(target.Host, _cts.Token);

            using var upstreamClient = await _socks.ConnectAsync(
                destination.Ip, target.Port, TimeSpan.FromSeconds(_config.SocksTimeoutSeconds), _cts.Token);

            await stream.WriteAsync(EstablishResponse, _cts.Token);
            _marker.Touch();
            if (_config.Verbose)
            {
                _log.Info($"forward-proxy: {target} -> {destination.Ip} ({destination.Source})");
            }

            var upstreamStream = new NetworkStream(upstreamClient, ownsSocket: false);
            await TunnelAsync(stream, upstreamStream);
        }
        catch (OperationCanceledException) when (!_running)
        {
            // остановка службы: закрываемся молча
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or FormatException)
        {
            Interlocked.Increment(ref _failedConnections);
            var detail = ex is OperationCanceledException
                ? "таймаут"
                : ex.Message;
            _log.Error($"forward-proxy: {client.Client.RemoteEndPoint}: {detail}");
            try
            {
                await client.GetStream().WriteAsync(BadGatewayResponse, CancellationToken.None);
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>Качает байты в обе стороны; конец одного направления гасит оба сокета.</summary>
    private static async Task TunnelAsync(NetworkStream client, NetworkStream upstream)
    {
        var up = PumpAsync(client, upstream);
        var down = PumpAsync(upstream, client);
        await Task.WhenAll(up, down);
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }

        try
        {
            if (to.Socket.Connected)
            {
                to.Socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (SocketException)
        {
        }
    }

    /// <summary>Читает заголовок до \r\n\r\n с ограничением размера; null — клиент ушёл молча.</summary>
    private static async Task<byte[]?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (!ContainsHeaderEnd(buffer.GetBuffer(), (int)buffer.Length))
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > ConnectRequestParser.MaxHeaderBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static bool ContainsHeaderEnd(byte[] buffer, int length)
    {
        var window = buffer.AsSpan(0, length);
        return window.IndexOf("\r\n\r\n"u8) >= 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
