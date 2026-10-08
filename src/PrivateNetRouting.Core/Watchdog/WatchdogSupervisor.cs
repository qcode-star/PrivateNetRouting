using System.Diagnostics;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>
/// Lifecycle of the idle stopper as seen from the outside. The GUI toggle starts a detached
/// worker — the same executable with the --watchdog-worker marker, spawned through cmd with
/// file redirection — that outlives the application and switches itself off after the idle
/// stop (WatchdogWorker); the foreground `pnr watchdog` command is the same stopper in a
/// terminal. Liveness is read from process command lines; the stop kills only the processes
/// identified this way. The stopper is never registered for autostart (rule #4 of AGENTS.md):
/// it exists only after an explicit start and disappears after doing its job.
/// </summary>
public sealed class WatchdogSupervisor(
    WatchdogConfig config,
    IProcessFinder processes,
    FileLog log,
    Func<string, string?, string, int?>? spawnWorker = null,
    Action<int>? killProcess = null,
    Func<string?>? currentExePath = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Marker of the detached worker in the command line; the same marker identifies the worker on stop.</summary>
    public const string WorkerMarker = "--watchdog-worker";

    /// <summary>The foreground CLI stopper: the `pnr watchdog` process (as in diagnostics).</summary>
    public const string CliProcessName = "pnr.exe";

    /// <summary>Executables of this installation that can host the worker (the GUI exe and the CLI exe); WMI names carry the extension.</summary>
    public static readonly IReadOnlyList<string> WorkerHostNames = ["PrivateNetRouting.App.exe", CliProcessName];

    private readonly Func<string, string?, string, int?> _spawnWorker =
        spawnWorker ?? ((commandLine, stdoutPath, stderrPath) => ProcessOps.StartViaCmd(commandLine, stdoutPath, stderrPath)?.Id ?? 0);

    private readonly Action<int> _killProcess =
        killProcess ?? (pid => Process.GetProcessById(pid).Kill());

    private readonly Func<string?> _currentExePath =
        currentExePath ?? (() => Environment.ProcessPath);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>Pids of the running stoppers: our executables with the worker marker, plus the CLI form. A static helper — diagnostics reads the same truth.</summary>
    public static IReadOnlyList<int> FindStopperPids(IProcessFinder finder) =>
        WorkerHostNames.SelectMany(name => finder.Find(name, WorkerMarker))
            .Concat(finder.Find(CliProcessName, "watchdog"))
            .Select(p => p.Id)
            .Distinct()
            .ToList();

    public IReadOnlyList<int> FindStopperPids() => FindStopperPids(processes);

    public bool IsRunning() => FindStopperPids().Count > 0;

    /// <summary>
    /// Idempotent start: a running stopper is left alone; otherwise the same executable with the
    /// marker is spawned through cmd (stdout to NUL, stderr to a file) and the start is confirmed
    /// by the process list — the same truth the chain card row reads.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var existing = FindStopperPids();
        if (existing.Count > 0)
        {
            log.Info($"watchdog: stopper is already running (pid {string.Join(", ", existing)}) — not starting another one");
            return;
        }

        var exe = _currentExePath()
            ?? throw new IOException("cannot resolve the current executable path to spawn the watchdog worker");
        var workerErrorLog = Path.Combine(
            Path.GetDirectoryName(log.FilePath) ?? AppContext.BaseDirectory,
            "watchdog.err.log");
        // (command line, stdout -> NUL, stderr)
        var pid = _spawnWorker($"{ProcessOps.Quote(exe)} {WorkerMarker}", null, workerErrorLog);
        log.Info($"watchdog: worker spawned via cmd, pid={pid}");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WorkerStartWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning())
            {
                log.Info("watchdog: worker is running");
                return;
            }

            await _delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        throw new IOException($"watchdog worker did not start within {config.WorkerStartWaitSeconds}s; log: {workerErrorLog}");
    }

    /// <summary>Stops the running stoppers (killing by pid from the command-line search) and waits for them to disappear.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        var pids = FindStopperPids();
        if (pids.Count == 0)
        {
            return;
        }

        foreach (var pid in pids)
        {
            try
            {
                _killProcess(pid);
                log.Info($"watchdog: stopped stopper pid={pid}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn($"watchdog: failed to stop stopper pid={pid}: {ex.Message}");
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WorkerStopWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (FindStopperPids().Count == 0)
            {
                return;
            }

            await _delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        log.Warn($"watchdog: stoppers are still alive {config.WorkerStopWaitSeconds}s after the kill: {string.Join(", ", FindStopperPids())}");
    }
}
