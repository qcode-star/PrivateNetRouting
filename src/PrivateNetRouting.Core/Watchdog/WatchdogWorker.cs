using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>
/// The body of the detached idle-stop worker — a single entry point for the host executable
/// (PrivateNetRouting.App --watchdog-worker; the argument is handled before everything else —
/// in App before the single-instance mutex and before the UI). It waits for the idle, stops
/// the WSL Claude host and the chain (the daemon shutdown may raise UAC per
/// chain.tailscale.elevation), and exits by itself; the log is watchdog-worker.log (shared with
/// the GUI and other writers — everyone writes to the same single file).
/// </summary>
public static class WatchdogWorker
{
    /// <summary>
    /// Why a freshly started worker must not run (null — it runs). A stopper exists only over a
    /// live chain, so a chain that is stopped at start means the chain went down after the spawn;
    /// and one stopper is enough: when several are alive the lowest pid stays (two workers that
    /// started together both see each other, so the rule must pick the same survivor for both).
    /// </summary>
    internal static string? RefusalReason(IReadOnlyList<int> stopperPids, int selfPid, ChainState chain)
    {
        if (stopperPids.Any(pid => pid != selfPid && pid < selfPid))
        {
            return "another stopper is already watching this chain";
        }

        return chain.IsStopped ? "the chain is not up, there is nothing to watch" : null;
    }

    public static async Task<int> RunAsync(CancellationToken stop)
    {
        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config, "watchdog-worker.log");
        runtime.Log.Info($"watchdog worker: started (pid {Environment.ProcessId}, interval {config.Watchdog.CheckSeconds}s)");
        try
        {
            var refusal = RefusalReason(
                runtime.WatchdogControl.FindStopperPids(),
                Environment.ProcessId,
                await runtime.Chain.GetStateAsync(stop).ConfigureAwait(false));
            if (refusal is not null)
            {
                runtime.Log.Info($"watchdog worker: {refusal} — exiting");
                return 0;
            }

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
