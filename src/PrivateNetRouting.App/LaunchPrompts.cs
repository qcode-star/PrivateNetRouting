using System.Windows;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core.Launcher;

namespace PrivateNetRouting.App;

/// <summary>
/// The restart offer shown before a launch whose target application is already running without
/// our proxy: both applications are single-instance, so the flags of a new start only activate
/// the window of the running copy and never reach its processes. "Yes" closes that copy and
/// starts it again through the proxy, "No" cancels the launch — the caller writes the refusal
/// to the log and reports it in its own result line.
/// </summary>
internal static class LaunchPrompts
{
    /// <summary>
    /// True — the launch goes ahead (closing the running copy when it carries no proxy of ours).
    /// The owner is asked only about a copy that works without the proxy; a copy started through
    /// the launcher needs no question — a new start just activates its window.
    /// </summary>
    public static bool ConfirmRestart(Window owner, RunningAppInfo running, string appNameKey)
    {
        if (running.State != RunningAppState.WithoutProxy)
        {
            return true;
        }

        var answer = MessageBox.Show(
            owner,
            Loc.F("Main.Launch.RestartOfferFmt", Loc.S(appNameKey), running.ProcessCount),
            Loc.S("Main.Launch.RestartOfferTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>The launch options matching the answer: RestartRunning is set only for a refused-free restart.</summary>
    public static LauncherOptions OptionsFor(RunningAppInfo running) =>
        new(CheckOnly: false, RestartRunning: running.State == RunningAppState.WithoutProxy);
}
