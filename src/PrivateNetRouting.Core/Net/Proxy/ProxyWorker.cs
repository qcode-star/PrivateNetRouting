using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>
/// The body of the forward-proxy detached worker — a single entry point for both hosts
/// (pnr --proxy-worker and PrivateNetRouting.App --proxy-worker; the argument is handled before
/// everything else — in App before the single-instance mutex and before the UI). It lives until the
/// chain shutdown kills it; the log is forward-proxy.log (shared with the GUI and other
/// writers — everyone writes to the same single file).
/// </summary>
public static class ProxyWorker
{
    public static async Task<int> RunAsync(CancellationToken stop)
    {
        var config = AppConfigStore.LoadOrCreate();
        if (!config.ForwardProxy.Enabled)
        {
            return 0; // the proxy is disabled by config — the worker has nothing to do
        }

        if (AppProxyEndpoint.IsSkipped(config))
        {
            return 0; // the skipped socks5 shape: no local proxy exists, the applications point at the external endpoint
        }

        await using var runtime = ProxyRuntime.FromConfig(config);
        runtime.Log.Info($"proxy worker: starting (pid {Environment.ProcessId})");
        if (runtime.ProxyService is null)
        {
            return 0; // the skipped shape (double guard: the supervisor never spawns this worker)
        }

        try
        {
            await runtime.ProxyService.StartAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            runtime.Log.Error($"proxy worker: start failed: {ex.Message}");
            return 1;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // in the App host the worker starts under the Dispatcher context: without ConfigureAwait
        // above, these lines would never execute (continuations — to the dispatcher queue)
        await runtime.ProxyService!.StopAsync().ConfigureAwait(false);
        runtime.Log.Info("proxy worker: stopped");
        return 0;
    }
}
