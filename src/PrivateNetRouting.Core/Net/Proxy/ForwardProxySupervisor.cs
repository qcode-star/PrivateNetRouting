using System.Diagnostics;
using System.Management;
using System.Net;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>
/// Надстройка над ForwardProxyService, решающая, где живёт слушатель: в этом процессе
/// или в отдельном detached-воркере (forwardProxy.detached — паритет ts-fwd прежней схемы,
/// прокси переживает выход запустившей его команды/GUI). Порт — единый ресурс: занятый
/// порт никогда не биндится вторым экземпляром (чей бы он ни был), останов добивает только
/// опознанного по командной строке воркера, чужой владелец не трогается (предупреждение
/// в лог). Живость для цепочки — порт: кто бы её ни спрашивал (GUI, pnr), ответ одинаков.
/// </summary>
public sealed class ForwardProxySupervisor(
    ForwardProxyConfig config,
    IForwardProxyInstance inner,
    IPortProbe ports,
    ITcpConnectionTable tcpTable,
    IProcessFinder processes,
    FileLog log,
    Func<string, string?, string, int?>? spawnWorker = null,
    Action<int>? killProcess = null,
    Func<string?>? currentExePath = null) : IProxyControl, IProxyStatistics
{
    /// <summary>Маркер detached-воркера в командной строке; им же воркер опознаётся при останове.</summary>
    public const string WorkerMarker = "--proxy-worker";

    private readonly Func<string, string?, string, int?> _spawnWorker =
        spawnWorker ?? ((commandLine, stdoutPath, stderrPath) => ProcessOps.StartViaCmd(commandLine, stdoutPath, stderrPath)?.Id ?? 0);

    private readonly Action<int> _killProcess =
        killProcess ?? (pid => Process.GetProcessById(pid).Kill());

    private readonly Func<string?> _currentExePath =
        currentExePath ?? (() => Environment.ProcessPath);

    private int Port => Socks5Client.ParseEndPoint(config.Listen).Port;

    public bool IsRunning => inner.IsRunning || ports.IsListening(Port);

    public IReadOnlyList<string> ListenerEndpoints =>
        inner.IsRunning
            ? inner.ListenerEndpoints
            : tcpTable.GetListeners(Port)
                .Select(entry => new IPEndPoint(entry.Address, entry.Port).ToString())
                .ToList();

    public int ActiveConnections => inner.ActiveConnections;

    public long TotalConnections => inner.TotalConnections;

    public long FailedConnections => inner.FailedConnections;

    public async Task StartAsync(CancellationToken ct)
    {
        if (inner.IsRunning)
        {
            return;
        }

        if (ports.IsListening(Port))
        {
            log.Info($"forward-proxy: port {Port} is already listening — reusing the running owner ({DescribeOwners()})");
            return;
        }

        if (config.Detached)
        {
            await StartWorkerAsync(ct);
            return;
        }

        await inner.StartAsync(ct);
    }

    /// <summary>Подъём detached-воркера (тот же исполняемый файл с маркером, через cmd с файловым редиректом) и ожидание его слушателя.</summary>
    private async Task StartWorkerAsync(CancellationToken ct)
    {
        var exe = _currentExePath()
            ?? throw new IOException("cannot resolve the current executable path to spawn the proxy worker");
        var workerErrorLog = Path.Combine(
            Path.GetDirectoryName(log.FilePath) ?? AppContext.BaseDirectory,
            "forward-proxy.err.log");
        // (командная строка, stdout -> NUL, stderr)
        var pid = _spawnWorker($"{ProcessOps.Quote(exe)} {WorkerMarker}", null, workerErrorLog);
        log.Info($"forward-proxy: worker spawned via cmd, pid={pid}");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WorkerStartWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (ports.IsListening(Port))
            {
                log.Info("forward-proxy: worker is listening");
                return;
            }

            await Task.Delay(250, ct);
        }

        throw new IOException($"proxy worker did not start listening within {config.WorkerStartWaitSeconds}s; log: {workerErrorLog}");
    }

    public async Task StopAsync()
    {
        if (inner.IsRunning)
        {
            await inner.StopAsync();
            return;
        }

        var owners = tcpTable.GetListeners(Port);
        if (owners.Count == 0)
        {
            return;
        }

        var workerPids = owners.Select(owner => owner.ProcessId).Distinct().Where(IsOurWorker).ToList();
        if (workerPids.Count == 0)
        {
            log.Warn($"forward-proxy: port {Port} is held by {DescribeOwners(owners)} — not our worker, leaving it running");
            return;
        }

        foreach (var pid in workerPids)
        {
            try
            {
                _killProcess(pid);
                log.Info($"forward-proxy: killed worker pid={pid}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn($"forward-proxy: failed to kill worker pid={pid}: {ex.Message}");
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WorkerStopWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!ports.IsListening(Port))
            {
                log.Info("forward-proxy: worker stopped, port released");
                return;
            }

            await Task.Delay(250);
        }

        log.Warn($"forward-proxy: port {Port} is still listening {config.WorkerStopWaitSeconds}s after the worker was killed");
    }

    /// <summary>
    /// Свой воркер: в командной строке владельца порта есть маркер. CommandLine элевейтнутого
    /// процесса не виден неэлевейтнутому читателю (WMI, Win11 24H2+) — тогда воркер не
    /// опознаётся и не убивается, останов цепочки честно предупреждает об этом в лог.
    /// </summary>
    private bool IsOurWorker(int pid)
    {
        try
        {
            return processes.FindById(pid)?.CommandLine.Contains(WorkerMarker, StringComparison.Ordinal) == true;
        }
        catch (Exception ex) when (ex is ManagementException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private string DescribeOwners() => DescribeOwners(tcpTable.GetListeners(Port));

    private static string DescribeOwners(IReadOnlyList<TcpListenerEntry> owners) =>
        string.Join(", ", owners
            .GroupBy(owner => owner.ProcessId)
            .Select(group => $"{group.First().ProcessName ?? "unknown"}({group.Key})"));
}
