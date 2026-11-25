using System.Diagnostics;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>Which desktop app the owner asked to stop.</summary>
public enum AppStopTarget
{
    Claude,
    Codex,
}

/// <summary>
/// Outcome of one manual stop: <see cref="Disabled"/> — the app is turned off in the config and
/// nothing was touched; otherwise the Windows close and, when the config names WSL distros, the
/// WSL host pass. <see cref="AnythingStopped"/> tells "nothing was running" from a real stop,
/// <see cref="SomethingSurvived"/> — that what was found did not fully go away.
/// </summary>
public sealed record AppStopResult(
    AppStopTarget Target,
    bool Disabled,
    RunningAppCloseResult Windows,
    WslHostStopResult? Wsl,
    TimeSpan Elapsed)
{
    public bool AnythingStopped => Windows.AnythingClosed || Wsl is { State: WslHostState.Stopped };

    public bool SomethingSurvived => Windows.Survived.Count > 0 || Wsl is { State: WslHostState.Stuck };
}

/// <summary>
/// The owner's manual stop of one desktop app: the Windows side is the launcher's own running-copy
/// machinery (its markers keep CLI processes that merely share the image name out), the WSL side is
/// the idle stopper's script with exactly that app's pass enabled — no new stop mechanism. The
/// Windows processes go first and the WSL hosts after them: a closing app may restart its WSL host
/// while its own process is still alive (observed live), so a WSL pass that ran alongside the close
/// would leave a fresh host behind. The WSL pass only visits distros that are already running, and
/// the chain is not touched at all. The Windows close is bounded by <c>restartGraceSeconds</c>, the
/// WSL pass by its own per-distro timeout; the app being stopped is the only thing this class ever
/// closes.
/// </summary>
public sealed class AppStopper(
    AppConfig config,
    ClaudeLauncher claude,
    CodexLauncher codex,
    IWslProcessRunner? wsl = null)
{
    private readonly IWslProcessRunner _wsl = wsl ?? new WslProcessRunner();

    /// <summary>
    /// Stops every process of <paramref name="target"/> on Windows and its hosts in the running
    /// configured WSL distros. A cancellation propagates to both halves (a running wsl.exe is
    /// killed with its tree); the GUI passes none — a stop that has started is left to finish.
    /// </summary>
    public async Task<AppStopResult> StopAsync(AppStopTarget target, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var enabled = target == AppStopTarget.Claude ? config.Claude.Enabled : config.Codex.Enabled;
        if (!enabled)
        {
            return new AppStopResult(target, true, RunningAppCloseResult.Nothing, null, watch.Elapsed);
        }

        var windows = target == AppStopTarget.Claude
            ? await claude.CloseRunningAsync(ct).ConfigureAwait(false)
            : await codex.CloseRunningAsync(ct).ConfigureAwait(false);

        // the WSL pass runs over the state the close left behind: an app that is still alive may
        // start a new WSL host during its own shutdown, and that host must be found too
        var wslResult = await StopWslAsync(target, ct).ConfigureAwait(false);

        LogWsl(target, wslResult);
        return new AppStopResult(target, false, windows, wslResult, watch.Elapsed);
    }

    /// <summary>The WSL pass for exactly one app; null — no distro is configured, the step is skipped.</summary>
    private async Task<WslHostStopResult?> StopWslAsync(AppStopTarget target, CancellationToken ct)
    {
        var distros = config.Wsl.Distros.Select(distro => distro.Name).ToList();
        if (distros.Count == 0)
        {
            return null;
        }

        var stopper = new WslAppHostStopper(
            distros,
            stopClaude: target == AppStopTarget.Claude,
            stopCodex: target == AppStopTarget.Codex,
            _wsl);
        return await stopper.StopAsync(ct).ConfigureAwait(false);
    }

    private void LogWsl(AppStopTarget target, WslHostStopResult? wsl)
    {
        if (wsl is null)
        {
            return;
        }

        var (name, prefix, log) = target == AppStopTarget.Claude
            ? ("Claude", "launcher", claude.Log)
            : ("Codex", "codex-launcher", codex.Log);
        var line = $"{prefix}: {name} WSL hosts: {wsl.State} {wsl.Detail}";
        if (wsl.State == WslHostState.Stuck)
        {
            log.Warn(line);
        }
        else
        {
            log.Info(line);
        }
    }
}
