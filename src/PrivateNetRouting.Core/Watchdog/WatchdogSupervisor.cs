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

    /// <summary>The CLI command of the foreground stopper (`pnr watchdog`).</summary>
    public const string CliCommand = "watchdog";

    /// <summary>
    /// Pids of the running stoppers: our executables with the worker marker, plus the CLI form. A
    /// static helper — diagnostics reads the same truth. The WMI lookup is a substring prefilter;
    /// the command line is then split into arguments and must carry the marker as an argument of its
    /// own (the GUI exe takes it anywhere, pnr — as its first argument) or be `pnr watchdog` —
    /// `pnr logs show watchdog` is not a stopper.
    /// </summary>
    public static IReadOnlyList<int> FindStopperPids(IProcessFinder finder) =>
        WorkerHostNames.SelectMany(name => finder.Find(name, WorkerMarker).Select(p => (Name: name, Process: p)))
            .Concat(finder.Find(CliProcessName, CliCommand).Select(p => (Name: CliProcessName, Process: p)))
            .Where(found => IsStopperCommandLine(found.Name, found.Process.CommandLine))
            .Select(found => found.Process.Id)
            .Distinct()
            .ToList();

    internal static bool IsStopperCommandLine(string processName, string commandLine)
    {
        var arguments = SplitArguments(commandLine);
        if (arguments.Count < 2)
        {
            return false;
        }

        if (string.Equals(processName, CliProcessName, StringComparison.OrdinalIgnoreCase))
        {
            // the CLI dispatches on its first argument, exactly
            return arguments[1] == WorkerMarker || arguments[1] == CliCommand;
        }

        // the GUI host recognizes the marker anywhere among its arguments (HeadlessHost)
        return arguments.Skip(1).Any(argument => string.Equals(argument, WorkerMarker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Splits a Windows command line the way the C runtime does (CommandLineToArgvW): the program
    /// name ends at the first unquoted space and takes no escapes; in the arguments 2n backslashes
    /// before a quote are n backslashes and a quote toggle, 2n+1 — n backslashes and a literal quote.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        var result = new List<string>();
        var i = 0;
        var text = commandLine;

        // the program name
        var name = new System.Text.StringBuilder();
        var quoted = false;
        while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else
            {
                name.Append(text[i]);
            }

            i++;
        }

        if (name.Length > 0)
        {
            result.Add(name.ToString());
        }

        while (true)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                return result;
            }

            var argument = new System.Text.StringBuilder();
            quoted = false;
            while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
            {
                var backslashes = 0;
                while (i < text.Length && text[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < text.Length && text[i] == '"')
                {
                    argument.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        argument.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }

                    i++;
                }
                else
                {
                    argument.Append('\\', backslashes);
                    if (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
                    {
                        argument.Append(text[i]);
                        i++;
                    }
                }
            }

            result.Add(argument.ToString());
        }
    }

    public IReadOnlyList<int> FindStopperPids() => FindStopperPids(processes);

    public bool IsRunning() => FindStopperPids().Count > 0;

    /// <summary>
    /// The auto-start of the last chain raise (still pending or done). The raise itself fires it
    /// and forgets it, but a command-line process exits right after the raise — it awaits this
    /// task first, or the worker spawn dies with the process and the stopper is never armed.
    /// </summary>
    public Task LastAutoStart { get; private set; } = Task.CompletedTask;

    /// <summary>Chain-raised hook: arms the stopper off the raise path (watchdog.autoStart).</summary>
    public void TrackAutoStart() =>
        LastAutoStart = Task.Run(() => AutoStartAsync(CancellationToken.None));

    /// <summary>
    /// Idempotent start: a running stopper is left alone; otherwise the same executable with the
    /// marker is spawned through cmd (stdout to NUL, stderr to a file) and the start is confirmed
    /// by the process list — the same truth the chain card row reads.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        // check -> spawn -> wait-for-visibility is one critical section across processes: the GUI
        // and a CLI raise close together would otherwise both see "no stopper" during the gap
        // between the spawn and the worker showing up in the process list, and arm two of them
        using var spawnLock = await AcquireSpawnLockAsync(ct).ConfigureAwait(false);
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

    /// <summary>How long a start waits for another start (this or another process) to finish its spawn.</summary>
    private static readonly TimeSpan SpawnLockWait = TimeSpan.FromSeconds(45);

    /// <summary>
    /// An exclusive file next to the logs - async-safe, unlike a named mutex (thread-affine), and
    /// shared between the GUI and the CLI. Fails open: a lock that cannot be taken in time is logged
    /// and the start goes on, a stuck lock file must never keep the chain without its stopper.
    /// </summary>
    private async Task<IDisposable> AcquireSpawnLockAsync(CancellationToken ct)
    {
        var path = Path.Combine(
            Path.GetDirectoryName(log.FilePath) ?? AppContext.BaseDirectory,
            "watchdog.spawn.lock");
        var deadline = DateTime.UtcNow + SpawnLockWait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    log.Warn($"watchdog: the spawn lock {path} is still held after {SpawnLockWait.TotalSeconds:0}s, starting without it: {ex.Message}");
                    return NoLock.Instance;
                }
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Automatic arming after a chain raise (watchdog.autoStart, default on): a running stopper
    /// is left alone, failures are logged and swallowed — the raise must not fail because of it.
    /// Never throws: it is invoked fire-and-forget from the chain orchestrator.
    /// </summary>
    public async Task AutoStartAsync(CancellationToken ct)
    {
        try
        {
            if (!config.AutoStart)
            {
                log.Debug("watchdog: auto start is off (watchdog.autoStart)");
                return;
            }

            if (IsRunning())
            {
                log.Debug("watchdog: stopper is already running — auto start skipped");
                return;
            }

            log.Info("watchdog: auto start after the chain raise");
            await StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn($"watchdog: auto start failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the running stoppers (killing by pid from the command-line search) and waits for them
    /// to disappear. The calling stopper is left alone: the worker reaches this stop through its
    /// own chain stop (WatchdogService) and exits right after — killing itself here would cut the
    /// final log lines and the CLI form's exit.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        // a raise right before this stop may still be spawning its worker, which is not in the
        // process list yet: let the spawn finish, or the worker would outlive the chain it guards
        try
        {
            await LastAutoStart.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn($"watchdog: the pending auto start failed: {ex.Message}");
        }

        var self = Environment.ProcessId;
        var pids = FindStopperPids().Where(pid => pid != self).ToList();
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
