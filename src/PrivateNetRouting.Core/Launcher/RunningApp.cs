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
    /// <summary>
    /// Pins the processes of the scan: from here on the closer acts on THOSE processes, not on
    /// whatever owns the PID later (a PID of an exited process is reused quickly, and a forced
    /// tree kill of a stranger — a compiler, a browser tab — is not recoverable).
    /// </summary>
    void Track(IReadOnlyList<int> processIds)
    {
    }

    /// <summary>WM_CLOSE to the main window; false — the process has no window (or is already gone).</summary>
    bool CloseGracefully(int processId);

    bool Exists(int processId);

    /// <summary>Forced kill of the process together with its tree.</summary>
    void KillTree(int processId);
}

/// <summary>
/// WM_CLOSE to the top-level windows of a process. Unlike Process.CloseMainWindow, which only
/// finds a visible, unowned main window, this reaches every window of the process — an
/// Electron app hidden to the tray still has its (hidden) main window, and is asked to close too.
/// The input-method windows the system creates in every GUI thread ("IME", "MSCTFIME UI") are
/// skipped: they are not the application's, and closing them only breaks its text input.
/// </summary>
internal static class WindowCloser
{
    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    /// <summary>Window classes the system owns in every GUI thread: never asked to close.</summary>
    private static readonly string[] SystemWindowClasses = ["IME", "MSCTFIME UI"];

    /// <summary>Posts WM_CLOSE to every top-level window of the process, hidden ones included; returns how many.</summary>
    public static int PostClose(int processId)
    {
        var posted = 0;
        EnumWindows(
            (hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == (uint)processId
                    && !IsSystemWindowClass(ClassName(hwnd))
                    && PostMessage(hwnd, WmClose, 0, 0))
                {
                    posted++;
                }

                return true;
            },
            0);
        return posted;
    }

    /// <summary>True for the IME windows the system adds to a GUI thread (not the application's own).</summary>
    internal static bool IsSystemWindowClass(string className) =>
        SystemWindowClasses.Contains(className, StringComparer.OrdinalIgnoreCase);

    private static string ClassName(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint hwnd, char[] className, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

/// <summary>
/// Closer over real processes; no special rights the applications do not have themselves.
/// <see cref="Track"/> opens a handle per process of the scan and keeps it: the handle keeps
/// identifying the original process after its PID has been reused, so a late kill can never hit
/// a stranger — a process that exited is "gone", and Kill on its handle only throws. A process
/// whose handle cannot be opened (access denied) is not acted on at all — no close, no kill, the
/// PID is not trusted; while a process holds that PID it is reported as a survivor.
/// </summary>
public sealed class WindowsRunningAppCloser : IRunningAppCloser, IDisposable
{
    private readonly Dictionary<int, Process> _tracked = [];

    /// <summary>Processes of the scan that could not be pinned (access denied): reported, never acted on.</summary>
    private readonly HashSet<int> _unpinned = [];

    public void Track(IReadOnlyList<int> processIds)
    {
        lock (_tracked)
        {
            ReleaseTracked();
            foreach (var processId in processIds)
            {
                Process process;
                try
                {
                    process = Process.GetProcessById(processId);
                }
                catch (ArgumentException)
                {
                    continue; // gone already: nothing to pin
                }

                try
                {
                    // GetProcessById opens nothing: the handle is opened (and held) here
                    ProcessOps.PinHandle(process);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    process.Dispose();
                    if (ex is Win32Exception)
                    {
                        _unpinned.Add(processId); // alive but not ours to open
                    }

                    continue;
                }

                if (!_tracked.TryAdd(processId, process))
                {
                    process.Dispose();
                }
            }
        }
    }

    public bool CloseGracefully(int processId)
    {
        try
        {
            var process = Handle(processId);
            return process is not null && !process.HasExited && WindowCloser.PostClose(process.Id) > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public bool Exists(int processId)
    {
        if (IsUnpinned(processId))
        {
            return PidIsRunning(processId);
        }

        try
        {
            var process = Handle(processId);
            return process is not null && !process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // access denied is not "gone": the process stays a survivor the caller reports
            return ex is Win32Exception;
        }
    }

    public void KillTree(int processId)
    {
        try
        {
            var process = Handle(processId);
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // gone or not ours: the caller re-checks and reports what survived
        }
    }

    public void Dispose()
    {
        lock (_tracked)
        {
            ReleaseTracked();
        }
    }

    private bool IsUnpinned(int processId)
    {
        lock (_tracked)
        {
            return _unpinned.Contains(processId);
        }
    }

    /// <summary>Whether some process holds the PID (an unpinned one cannot be told from a stranger, it is only reported).</summary>
    private static bool PidIsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The pinned process of this PID; a PID that was never tracked is not acted on.</summary>
    private Process? Handle(int processId)
    {
        lock (_tracked)
        {
            return _tracked.GetValueOrDefault(processId);
        }
    }

    private void ReleaseTracked()
    {
        foreach (var process in _tracked.Values)
        {
            process.Dispose();
        }

        _tracked.Clear();
        _unpinned.Clear();
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
/// app shares the Codex executable name, and the native Claude Code CLI is a `claude.exe` too),
/// so optional command-line markers — any one of them — filter the scan: the MSIX install path
/// (see <see cref="PackageInstallMarker"/>) and the execution alias the launcher itself starts
/// the app through. Closing is never started on its own initiative: the GUI and the CLI
/// ask the owner first, the launcher only executes the decision.
/// </summary>
public sealed class RunningApp(
    IProcessFinder finder,
    string processNamePart,
    IReadOnlyList<string>? commandLineMarkers,
    string proxyUrl,
    IRunningAppCloser closer,
    FileLog log,
    string logPrefix)
{
    private const string ProxyFlag = "--proxy-server=";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The path fragment every process of an MSIX package carries in its image path
    /// (`…\WindowsApps\&lt;package&gt;_&lt;version&gt;_…\app\&lt;exe&gt;`): the package name plus the
    /// separators keeps a CLI or another application that merely shares the executable name out.
    /// </summary>
    public static string PackageInstallMarker(string packageName) => $@"\WindowsApps\{packageName}_";

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

        if (commandLineMarkers is not null)
        {
            processes = processes
                .Where(p => commandLineMarkers.Any(m => p.CommandLine.Contains(m, StringComparison.OrdinalIgnoreCase)))
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

        // pin the scanned processes before anything is asked or killed: the waits below can take
        // seconds, and a PID freed meanwhile may belong to somebody else by the time of the kill
        closer.Track(processes.Select(p => p.Id).ToList());
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
