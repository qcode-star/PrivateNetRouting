using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>
/// The body of the detached idle-stop worker — a single entry point for the host executable
/// (PrivateNetRouting.App --watchdog-worker; the argument is handled before everything else —
/// in App before the single-instance mutex and before the UI). It waits for the idle, stops
/// the WSL Claude host and the chain (the daemon shutdown may raise UAC per
/// chain.daemonElevation), and exits by itself; the log is watchdog-worker.log (shared with
/// the GUI and other writers — everyone writes to the same single file).
/// </summary>
public static class WatchdogWorker
{
    public static async Task<int> RunAsync(CancellationToken stop)
    {
        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config, "watchdog-worker.log");
        runtime.Log.Info($"watchdog worker: started (pid {Environment.ProcessId}, interval {config.Watchdog.CheckSeconds}s)");
        try
        {
            await runtime.Watchdog.RunAsync(null, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            runtime.Log.Info("watchdog worker: cancelled");
            return 0;
        }
        catch (Exception ex)
        {
            runtime.Log.Error($"watchdog worker: failed: {ex.Message}");
            return 1;
        }

        runtime.Log.Info("watchdog worker: idle stop complete, exiting");
        return 0;
    }
}
