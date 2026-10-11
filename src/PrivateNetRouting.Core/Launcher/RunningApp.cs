using System.ComponentModel;
using System.Diagnostics;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>
/// State of an application the launcher is about to start. Claude Desktop and the Codex desktop
/// app are single-instance Electron/MSIX apps: a second start hands its arguments to the running
/// copy and exits, so the proxy flags of the new start never reach the processes of the old one.
/// </summary>
public enum RunningAppState
{
    /// <summary>No process of the application is running.</summary>
    NotRunning,

    /// <summary>Running with our --proxy-server: a second start only activates the window.</summary>
    Proxied,

    /// <summary>Running without our proxy (no flag at all, or another address): the launch cannot fix it.</summary>
    WithoutProxy,
}

public sealed record RunningAppInfo(RunningAppState State, IReadOnlyList<FoundProcess> Processes, string? ForeignProxyUrl)
{
    public static RunningAppInfo NotRunning { get; } = new(RunningAppState.NotRunning, [], null);

    public bool Running => State != RunningAppState.NotRunning;

    public int ProcessCount => Processes.Count;
}

/// <summary>Closing the processes of a foreign application: a graceful window close first, a forced tree kill as the fallback.</summary>
public interface IRunningAppCloser
{
    /// <summary>WM_CLOSE to the main window; false — the process has no window (or is already gone).</summary>
    bool CloseGracefully(int processId);

    bool Exists(int processId);

    /// <summary>Forced kill of the process together with its tree.</summary>
    void KillTree(int processId);
}

/// <summary>Closer over real processes; no special rights the applications do not have themselves.</summary>
public sealed class WindowsRunningAppCloser : IRunningAppCloser
{
    public bool CloseGracefully(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public bool Exists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // access denied is not "gone": the process stays a survivor the caller reports
            return ex is Win32Exception;
        }
    }

    public void KillTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // gone or not ours: the caller re-checks and reports what survived
        }
    }
}

public sealed record RunningAppCloseResult(int ClosedGracefully, int Killed, IReadOnlyList<int> Survived)
{
    public static RunningAppCloseResult Nothing { get; } = new(0, 0, []);

    public bool AnythingClosed => ClosedGracefully + Killed > 0;
}

/// <summary>
/// Already-running detection and shutdown of one application. Detection counts only the
/// processes of the application itself: a bare process name is not enough (the official ChatGPT
/// app shares the Codex executable name), so an optional command-line marker — the MSIX package
/// name — filters the scan. Closing is never started on its own initiative: the GUI and the CLI
/// ask the owner first, the launcher only executes the decision.
/// </summary>
public sealed class RunningApp(
    IProcessFinder finder,
    string processNamePart,
    string? commandLineMarker,
    string proxyUrl,
    IRunningAppCloser closer,
    FileLog log,
    string logPrefix)
{
    private const string ProxyFlag = "--proxy-server=";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(3);

    /// <summary>Scan for the processes of the application and compare their flags with our proxy.</summary>
    public RunningAppInfo Inspect()
    {
        IReadOnlyList<FoundProcess> processes;
        try
        {
            processes = finder.FindByNameContains(processNamePart);
        }
        catch (Exception ex)
        {
            log.Warn($"{logPrefix}: process scan failed: {ex.Message}");
            return RunningAppInfo.NotRunning;
        }

        if (commandLineMarker is not null)
        {
            processes = processes
                .Where(p => p.CommandLine.Contains(commandLineMarker, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (processes.Count == 0)
        {
            return RunningAppInfo.NotRunning;
        }

        string? foreign = null;
        var ours = false;
        foreach (var process in processes)
        {
            var proxy = ParseProxyUrl(process.CommandLine);
            if (proxy is null)
            {
                continue;
            }

            if (string.Equals(proxy, proxyUrl, StringComparison.OrdinalIgnoreCase))
            {
                ours = true;
            }
            else
            {
                foreign ??= proxy;
            }
        }

        return ours
            ? new RunningAppInfo(RunningAppState.Proxied, processes, null)
            : new RunningAppInfo(RunningAppState.WithoutProxy, processes, foreign);
    }

    /// <summary>What to write to the log when the launch goes ahead without a restart.</summary>
    public void Report(RunningAppInfo info, string appName)
    {
        if (!info.Running)
        {
            return;
        }

        if (info.State == RunningAppState.Proxied)
        {
            log.Info($"{logPrefix}: {appName} is already running ({info.ProcessCount} processes) — starting one more instance activates the existing window");
            return;
        }

        var detail = info.ForeignProxyUrl is null ? "without --proxy-server" : $"with a different proxy [{info.ForeignProxyUrl}]";
        log.Warn($"{logPrefix}: {appName} is already running ({info.ProcessCount} processes) {detail} — close it and use the launcher again, otherwise the traffic goes around the proxy");
    }

    /// <summary>What to write to the log when the owner has refused the restart offer.</summary>
    public void NoteDeclined(string appName) =>
        log.Info($"{logPrefix}: {appName} restart declined by the owner — nothing is started, the running copy keeps its own flags");

    /// <summary>
    /// Closes the running copy: WM_CLOSE to the main window first (the children go down with it),
    /// then a forced tree kill of whatever is still alive. The caller has already asked the owner.
    /// </summary>
    public async Task<RunningAppCloseResult> CloseAsync(string appName, TimeSpan graceful, CancellationToken ct)
    {
        var processes = Inspect().Processes;
        if (processes.Count == 0)
        {
            return RunningAppCloseResult.Nothing;
        }

        var asked = processes.Count(p => closer.CloseGracefully(p.Id));
        var pending = processes.Select(p => p.Id).ToList();
        if (asked > 0)
        {
            pending = await WaitUntilGoneAsync(pending, graceful, ct).ConfigureAwait(false);
        }

        var forced = pending.ToList();
        foreach (var processId in forced)
        {
            closer.KillTree(processId);
        }

        var survived = forced.Count == 0
            ? []
            : await WaitUntilGoneAsync(forced, KillWait, ct).ConfigureAwait(false);

        log.Info($"{logPrefix}: {appName} closed to restart through the proxy ({processes.Count} processes: window close {asked}, forced {forced.Count - survived.Count})");
        if (survived.Count > 0)
        {
            log.Warn($"{logPrefix}: {survived.Count} {appName} processes survived the close ({string.Join(", ", survived)}) — the new start will only activate the existing window");
        }

        return new RunningAppCloseResult(asked, forced.Count - survived.Count, survived);
    }

    /// <summary>The --proxy-server value of a command line; null — the flag is absent.</summary>
    internal static string? ParseProxyUrl(string commandLine)
    {
        var index = commandLine.IndexOf(ProxyFlag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = commandLine[(index + ProxyFlag.Length)..];
        var end = value.IndexOfAny([' ', '"', '\'']);
        return end < 0 ? value : value[..end];
    }

    /// <summary>Waits until the processes are gone; returns the PIDs still alive at the end.</summary>
    private async Task<List<int>> WaitUntilGoneAsync(List<int> processIds, TimeSpan limit, CancellationToken ct)
    {
        var pending = processIds.Where(closer.Exists).ToList();
        var deadline = DateTime.UtcNow + limit;
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            pending = pending.Where(closer.Exists).ToList();
        }

        return pending;
    }
}
