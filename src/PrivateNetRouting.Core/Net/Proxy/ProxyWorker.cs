using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>
/// Тело detached-воркера forward-прокси — единая точка входа для обоих хостов
/// (pnr --proxy-worker и PrivateNetRouting.App --proxy-worker; аргумент обрабатывается
/// до всего прочего — в App до mutex одиночного экземпляра и до UI). Живёт, пока его
/// не убьёт останов цепочки; лог — forward-proxy.log (или персональный
/// &lt;имя&gt;.&lt;pid&gt;.log, если файл уже держит другой процесс).
/// </summary>
public static class ProxyWorker
{
    public static async Task<int> RunAsync(CancellationToken stop)
    {
        var config = AppConfigStore.LoadOrCreate();
        if (!config.ForwardProxy.Enabled)
        {
            return 0; // прокси выключен конфигом — воркеру нечего делать
        }

        await using var runtime = ProxyRuntime.FromConfig(config);
        runtime.Log.Info($"proxy worker: starting (pid {Environment.ProcessId})");
        try
        {
            await runtime.ProxyService.StartAsync(stop);
        }
        catch (Exception ex)
        {
            runtime.Log.Error($"proxy worker: start failed: {ex.Message}");
            return 1;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stop);
        }
        catch (OperationCanceledException)
        {
        }

        await runtime.ProxyService.StopAsync();
        runtime.Log.Info("proxy worker: stopped");
        return 0;
    }
}
