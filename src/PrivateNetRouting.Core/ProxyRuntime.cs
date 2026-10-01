using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Runtime;

namespace PrivateNetRouting.Core;

/// <summary>
/// Композиция forward-прокси из конфига — единая точка сборки для WPF-приложения и CLI.
/// Владеет логом, пулом DoH и самим прокси; Dispose останавливает всё.
/// </summary>
public sealed class ProxyRuntime : IAsyncDisposable
{
    public AppConfig Config { get; }

    public ForwardProxyService Proxy { get; }

    public FileLog Log { get; }

    public DohEndpointPool DohPool { get; }

    private ProxyRuntime(AppConfig config, FileLog log, DohEndpointPool pool, ForwardProxyService proxy)
    {
        Config = config;
        Log = log;
        DohPool = pool;
        Proxy = proxy;
    }

    public static ProxyRuntime FromConfig(AppConfig config, string logFileName = "forward-proxy.log")
    {
        var fp = config.ForwardProxy;
        var log = new FileLog(
            Path.Combine(config.Logging.Directory, logFileName),
            config.Logging.MaxBytes,
            config.Logging.Keep);

        var socks = new Socks5Client(fp.Socks5Upstream);

        var seed = fp.DohSeed
            .Select(s => DohEndpoint.TryParse(s, out var ep) && ep is not null ? ep : null)
            .OfType<DohEndpoint>()
            .ToList();
        var rejected = fp.DohSeed.Count - seed.Count;
        if (rejected > 0)
        {
            log.Warn($"doh: пропущено некорректных seed-эндпоинтов: {rejected}");
        }

        IDohEndpointSource? source = fp.DohDiscovery.Enabled
            ? new TailscaleNetmapDohSource(fp.DohDiscovery)
            : null;
        var pool = new DohEndpointPool(
            socks,
            seed,
            log,
            TimeSpan.FromSeconds(fp.DohRefreshSeconds),
            TimeSpan.FromSeconds(fp.DohProbeTimeoutSeconds),
            TimeSpan.FromSeconds(fp.DohDeadCooldownSeconds),
            fp.DohProbeName,
            source);

        var resolver = new CachingDnsResolver(
            new DohResolver(socks, pool, TimeSpan.FromSeconds(fp.DohTimeoutSeconds)),
            TimeSpan.FromSeconds(fp.CacheTtlMinSeconds),
            TimeSpan.FromSeconds(fp.CacheTtlMaxSeconds));
        var marker = new ActivityMarker(
            fp.ActivityMarkerPath,
            TimeSpan.FromSeconds(fp.ActivityTouchIntervalSeconds));

        var proxy = new ForwardProxyService(fp, resolver, socks, marker, log);
        return new ProxyRuntime(config, log, pool, proxy);
    }

    public Task StartAsync(CancellationToken ct = default) => Proxy.StartAsync(ct);

    public Task StopAsync() => Proxy.StopAsync();

    public Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var fp = Config.ForwardProxy;
        var endpoint = Socks5Client.ParseEndPoint(fp.Listen);
        return ProxyHealthCheck.CheckAsync(
            endpoint.Address.ToString(),
            endpoint.Port,
            fp.HealthCheckHost,
            fp.HealthCheckPort,
            timeout,
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        DohPool.Dispose();
        Log.Dispose();
    }
}
