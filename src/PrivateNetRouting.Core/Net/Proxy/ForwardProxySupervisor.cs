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
/// A wrapper over ForwardProxyService that decides where the listener lives: in this process or
/// in a separate detached worker (forwardProxy.detached,
/// the proxy outlives the exit of the command/GUI that started it). The port is a single resource:
/// a busy port is never bound by a second instance (whosever it is), the shutdown kills only a
/// worker identified by its command line, a foreign owner is not touched (warning in the log).
/// Liveness for the chain is the port: whoever asks (GUI, pnr), the answer is the same.
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
    /// <summary>Marker of the detached worker in the command line; the same marker identifies the worker on shutdown.</summary>
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
            await StartWorkerAsync(ct).ConfigureAwait(false);
            return;
        }

        await inner.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Bring-up of the detached worker (the same executable with the marker, via cmd with file redirection) and waiting for its listener.</summary>
    private async Task StartWorkerAsync(CancellationToken ct)
    {
        var exe = _currentExePath()
            ?? throw new IOException("cannot resolve the current executable path to spawn the proxy worker");
        var workerErrorLog = Path.Combine(
            Path.GetDirectoryName(log.FilePath) ?? AppContext.BaseDirectory,
            "forward-proxy.err.log");
        // (command line, stdout -> NUL, stderr)
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

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw new IOException($"proxy worker did not start listening within {config.WorkerStartWaitSeconds}s; log: {workerErrorLog}");
    }

    public async Task StopAsync()
    {
        if (inner.IsRunning)
        {
            await inner.StopAsync().ConfigureAwait(false);
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

            await Task.Delay(250).ConfigureAwait(false);
        }

        log.Warn($"forward-proxy: port {Port} is still listening {config.WorkerStopWaitSeconds}s after the worker was killed");
    }

    /// <summary>
    /// Our worker: the port owner's command line contains the marker. The CommandLine of an elevated
    /// process is not visible to a non-elevated reader (WMI, Win11 24H2+) — then the worker is not
    /// identified and not killed, and the chain shutdown honestly warns about it in the log.
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
