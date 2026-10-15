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
/// In the skipped shapes (the http mode, or socks5 + forwardProxy.enabled=false) there is no
/// local proxy at all: the supervisor becomes an honest no-op — never running, nothing to
/// start/stop/detach.
/// </summary>
public sealed class ForwardProxySupervisor(
    ForwardProxyConfig config,
    IForwardProxyInstance? inner,
    IPortProbe ports,
    ITcpConnectionTable tcpTable,
    IProcessFinder processes,
    FileLog log,
    Func<string, string?, string, int?>? spawnWorker = null,
    Action<int>? killProcess = null,
    Func<string?>? currentExePath = null,
    bool skipped = false,
    string? skippedAppsEndpoint = null,
    string? skippedReason = null,
    IProxyStopChannel? stopChannel = null,
    Func<int, bool>? processAlive = null) : IProxyControl, IProxyStatistics
{
    /// <summary>Marker of the detached worker in the command line; the same marker identifies the worker on shutdown.</summary>
    public const string WorkerMarker = "--proxy-worker";

    private readonly Func<string, string?, string, int?> _spawnWorker =
        spawnWorker ?? ((commandLine, stdoutPath, stderrPath) => ProcessOps.StartViaCmd(commandLine, stdoutPath, stderrPath)?.Id ?? 0);

    private readonly Action<int> _killProcess =
        killProcess ?? (pid => Process.GetProcessById(pid).Kill(entireProcessTree: true));

    /// <summary>Whether the spawned process (the cmd wrapper, which lives as long as the worker does) still runs.</summary>
    private readonly Func<int, bool> _processAlive = processAlive ?? IsProcessAlive;

    private readonly Func<string?> _currentExePath =
        currentExePath ?? (() => Environment.ProcessPath);

    private int Port => Socks5Client.ParseEndPoint(config.Listen).Port;

    public bool IsRunning => !skipped && (inner!.IsRunning || ports.IsListening(Port));

    public IReadOnlyList<string> ListenerEndpoints =>
        skipped
            ? []
            : inner!.IsRunning
                ? inner.ListenerEndpoints
                : tcpTable.GetListeners(Port)
                    .Select(entry => new IPEndPoint(entry.Address, entry.Port).ToString())
                    .ToList();

    public bool CountersAreLocal => skipped || inner!.IsRunning || !ports.IsListening(Port);

    public int ActiveConnections => skipped ? 0 : inner!.ActiveConnections;

    public long TotalConnections => skipped ? 0 : inner!.TotalConnections;

    public long FailedConnections => skipped ? 0 : inner!.FailedConnections;

    public async Task StartAsync(CancellationToken ct)
    {
        if (skipped)
        {
            log.Info(SkippedMessage);
            return;
        }

        if (inner!.IsRunning)
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
        StartListeningForStopRequests();
    }

    private IDisposable? _stopListener;

    /// <summary>
    /// An in-process listener can be stopped only from inside this process, but the idle stopper
    /// is a separate process: it asks through the stop channel (see StopAsync) and this process
    /// stops its own listener.
    /// </summary>
    private void StartListeningForStopRequests()
    {
        StopListeningForStopRequests();
        _stopListener = stopChannel?.Listen(Port, HandleStopRequestAsync);
    }

    private void StopListeningForStopRequests()
    {
        _stopListener?.Dispose();
        _stopListener = null;
    }

    private async Task HandleStopRequestAsync()
    {
        try
        {
            log.Info("forward-proxy: another process (the idle stopper) asked this one to stop its listener");
            StopListeningForStopRequests();
            await inner!.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn($"forward-proxy: stopping the listener on request failed: {ex.Message}");
        }
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
        // the worker listens where this supervisor waits (and where the clients were pointed):
        // its own read of the config on disk may have been edited since this process started
        var listen = Socks5Client.ParseEndPoint(config.Listen);
        var pid = _spawnWorker($"{ProcessOps.Quote(exe)} {WorkerMarker} {ProxyWorker.ListenOption} {listen}", null, workerErrorLog);
        log.Info($"forward-proxy: worker spawned via cmd, pid={pid}");

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WorkerStartWaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (ports.IsListening(Port))
                {
                    log.Info("forward-proxy: worker is listening");
                    return;
                }

                ct.ThrowIfCancellationRequested();
                if (pid is > 0 && !_processAlive(pid.Value))
                {
                    // a config error or a busy port: waiting out the deadline would only delay the bad news
                    throw new IOException($"proxy worker exited before it started listening; log: {workerErrorLog}");
                }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            KillSpawnedWorker(pid, "the start was cancelled");
            throw;
        }

        KillSpawnedWorker(pid, $"it did not start listening within {config.WorkerStartWaitSeconds}s");
        throw new IOException($"proxy worker did not start listening within {config.WorkerStartWaitSeconds}s; log: {workerErrorLog}");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A worker whose start was abandoned (timeout, cancellation) must not bind the port later,
    /// behind the back of a bring-up that already reported its failure. The spawned process is the
    /// cmd wrapper, so the whole tree goes.
    /// </summary>
    private void KillSpawnedWorker(int? pid, string reason)
    {
        if (pid is not > 0)
        {
            return;
        }

        try
        {
            _killProcess(pid.Value);
            log.Warn($"forward-proxy: killed the spawned worker pid={pid} — {reason}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone (it exited on its own, e.g. on a busy port or a config error)
            log.Debug($"forward-proxy: spawned worker pid={pid} was not killed: {ex.Message}");
        }
    }

    /// <summary>
    /// Hands the listener over to a detached worker so it survives this process's exit (the tray
    /// "Exit" with the chain kept): the in-process instance is stopped and a worker takes the port,
    /// so clients see only a short gap. A listener already held by our worker — or by anyone else —
    /// is left as it is. In the skipped shape there is nothing of ours on the port — a no-op.
    /// </summary>
    public async Task DetachAsync(CancellationToken ct)
    {
        if (skipped)
        {
            log.Info(SkippedMessage);
            return;
        }

        if (!inner!.IsRunning)
        {
            if (ports.IsListening(Port))
            {
                log.Info($"forward-proxy: detach not needed — the port is held by {DescribeOwners()}");
            }

            return;
        }

        log.Info("forward-proxy: handing the listener over to a detached worker");
        StopListeningForStopRequests();
        await inner.StopAsync().ConfigureAwait(false);
        await StartWorkerAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (skipped)
        {
            return; // the skipped shape has no local proxy: nothing of ours is on the port
        }

        if (inner!.IsRunning)
        {
            StopListeningForStopRequests();
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
            if (stopChannel is not null && stopChannel.Request(Port))
            {
                // an in-process owner of ours (the GUI in the tray, a foreground pnr proxy)
                log.Info($"forward-proxy: port {Port} is held by {DescribeOwners(owners)} — asked it to stop its listener");
                await WaitForPortReleaseAsync("in-process owner stopped").ConfigureAwait(false);
                return;
            }

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

        await WaitForPortReleaseAsync("worker stopped").ConfigureAwait(false);
    }

    private async Task WaitForPortReleaseAsync(string stoppedMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WorkerStopWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!ports.IsListening(Port))
            {
                log.Info($"forward-proxy: {stoppedMessage}, port released");
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        log.Warn($"forward-proxy: port {Port} is still listening {config.WorkerStopWaitSeconds}s after the stop was requested");
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

    private string SkippedMessage =>
        skippedReason
        ?? $"forward-proxy: skipped in the socks5 mode (forwardProxy.enabled=false) — the applications point at {skippedAppsEndpoint ?? "chain.socks5.upstream"}";

    private static string DescribeOwners(IReadOnlyList<TcpListenerEntry> owners) =>
        string.Join(", ", owners
            .GroupBy(owner => owner.ProcessId)
            .Select(group => $"{group.First().ProcessName ?? "unknown"}({group.Key})"));
}
