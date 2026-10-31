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
    /// Why a freshly started stopper must not run (null — it runs). A stopper exists only over a
    /// live chain, so a chain that is stopped at start means the chain went down after the spawn;
    /// and one stopper is enough. The foreground `pnr watchdog` refuses beside any other stopper.
    /// A detached worker steps aside only for a worker with a lower pid (two workers that started
    /// together both see each other, so the rule must pick the same survivor for both); it does
    /// not yield to a foreground stopper, which refuses on its side — a worker and a `pnr watchdog`
    /// started together leave the worker, never neither of them. Pids are not monotonic on
    /// Windows, so the order between the two forms cannot rest on them.
    /// </summary>
    public static string? RefusalReason(IReadOnlyList<RunningStopper> stoppers, int selfPid, bool selfForeground, ChainState chain)
    {
        var others = stoppers.Where(stopper => stopper.Pid != selfPid).ToList();
        if (selfForeground && others.Count > 0)
        {
            return $"another stopper is already watching this chain (pid {string.Join(", ", others.Select(stopper => stopper.Pid))})";
        }

        if (!selfForeground && others.Any(stopper => !stopper.Foreground && stopper.Pid < selfPid))
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
                runtime.WatchdogControl.FindStoppers(),
                Environment.ProcessId,
                selfForeground: false,
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
