using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Net.Socks;

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
    /// <summary>
    /// "--listen host:port" after the marker: the address the spawning process waits on, which
    /// overrides forwardProxy.listen of the config read from disk (it may have changed since).
    /// </summary>
    public const string ListenOption = "--listen";

    public static async Task<int> RunAsync(CancellationToken stop, IReadOnlyList<string>? args = null)
    {
        var config = AppConfigStore.LoadOrCreate();
        var listenOverride = ParseListenOverride(args);
        if (listenOverride is not null)
        {
            config.ForwardProxy.Listen = listenOverride;
        }

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

    /// <summary>The value after --listen, or null when absent or not a valid endpoint (the config's own value then stands).</summary>
    internal static string? ParseListenOverride(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], ListenOption, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                return Socks5Client.ParseEndPoint(args[i + 1]).ToString();
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }
}
