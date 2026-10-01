using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Proxy;

namespace PrivateNetRouting.Core.Chain;

public enum ChainStepStatus
{
    Pending,
    Active,
    Done,
    Skipped,
    Failed,
}

/// <summary>Шаг подъёма/останова для UI и CLI: компонент, состояние, деталь.</summary>
public sealed record ChainStep(string Component, ChainStepStatus Status, string Detail);

/// <summary>Снимок состояния всех звеньев цепочки (только чтение).</summary>
public sealed record ChainState(
    bool DaemonHttpListening,
    bool DaemonProcessAlive,
    bool KeeperRunning,
    string? BackendState,
    bool ProxyRunning,
    bool ProxyHealthy)
{
    public bool IsFullyUp =>
        DaemonHttpListening
        && DaemonProcessAlive
        && KeeperRunning
        && BackendState == "Running"
        && ProxyRunning
        && ProxyHealthy;
}

/// <summary>Управление прокси со стороны оркестратора (реализация — ForwardProxyService).</summary>
public interface IProxyControl
{
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}

/// <summary>
/// Оркестратор цепочки: идемпотентный подъём (демон → кипер → узел → прокси → здоровье)
/// и полный останов (down → демон → кипер → прокси). Порядок и признаки живости — как в
/// прежней схеме: слушатель HTTP демона, процесс watch-ipn, BackendState=Running,
/// слушатель прокси, живой CONNECT-roundtrip.
/// </summary>
public sealed class ChainSupervisor(
    ChainConfig config,
    IProxyControl proxy,
    ISidecarDaemon daemon,
    ISidecarKeeper keeper,
    ISidecarCli cli,
    FileLog log,
    Func<CancellationToken, Task<bool>> healthCheck)
{
    public async Task<ChainState> GetStateAsync(CancellationToken ct)
    {
        var backendState = await cli.GetBackendStateAsync(ct);
        var proxyRunning = proxy.IsRunning;
        var proxyHealthy = proxyRunning && await healthCheck(ct);
        return new ChainState(
            daemon.IsHttpListening,
            daemon.IsProcessAlive,
            keeper.IsRunning,
            backendState,
            proxyRunning,
            proxyHealthy);
    }

    public async Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        // 1. демон (единственное возможное окно UAC на холодном старте)
        if (daemon.IsHttpListening && daemon.IsProcessAlive)
        {
            Report(progress, "Демон", ChainStepStatus.Skipped, "уже слушает");
        }
        else
        {
            Report(progress, "Демон", ChainStepStatus.Active, "поднимаю (подтверди UAC)");
            try
            {
                await daemon.EnsureStartedAsync(new Progress<string>(detail =>
                    Report(progress, "Демон", ChainStepStatus.Active, detail)), ct);
                Report(progress, "Демон", ChainStepStatus.Done, "слушатель поднят");
            }
            catch (Exception ex)
            {
                Report(progress, "Демон", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("демон", ex);
            }
        }

        // 2. кипер watch-ipn
        if (keeper.IsRunning)
        {
            Report(progress, "Кипер", ChainStepStatus.Skipped, "уже работает");
        }
        else
        {
            Report(progress, "Кипер", ChainStepStatus.Active, "запускаю watch-ipn");
            try
            {
                await keeper.EnsureStartedAsync(ct);
                Report(progress, "Кипер", ChainStepStatus.Done, "работает");
            }
            catch (Exception ex)
            {
                Report(progress, "Кипер", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("кипер", ex);
            }
        }

        // 3. узел (вход ключом из statedir, молчаливый)
        var state = await cli.GetBackendStateAsync(ct);
        if (state == "Running")
        {
            Report(progress, "Узел", ChainStepStatus.Skipped, "уже Running");
        }
        else
        {
            Report(progress, "Узел", ChainStepStatus.Active, "tailscale up");
            try
            {
                var up = await cli.UpAsync(ct);
                state = await cli.GetBackendStateAsync(ct);
                if (state != "Running")
                {
                    throw new IOException($"up не привёл к Running (сейчас {state ?? "нет ответа"}): {up.Output}");
                }

                Report(progress, "Узел", ChainStepStatus.Done, "Running");
            }
            catch (Exception ex) when (ex is not ChainException)
            {
                Report(progress, "Узел", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("узел", ex);
            }
        }

        // 4. forward-прокси (наш порт ts-fwd)
        if (proxy.IsRunning)
        {
            Report(progress, "Прокси", ChainStepStatus.Skipped, "уже работает");
        }
        else
        {
            Report(progress, "Прокси", ChainStepStatus.Active, "запускаю слушателя");
            try
            {
                await proxy.StartAsync(ct);
                Report(progress, "Прокси", ChainStepStatus.Done, "слушает");
            }
            catch (Exception ex) when (ex is not ChainException)
            {
                Report(progress, "Прокси", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("прокси", ex);
            }
        }

        // 5. здоровье: CONNECT-roundtrip через весь путь
        Report(progress, "Здоровье", ChainStepStatus.Active, "CONNECT-roundtrip");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.HealthWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await healthCheck(ct))
            {
                Report(progress, "Здоровье", ChainStepStatus.Done, "цепочка жива");
                log.Info("chain: полностью поднята");
                return;
            }

            await Task.Delay(1000, ct);
        }

        Report(progress, "Здоровье", ChainStepStatus.Failed, "CONNECT не прошёл");
        throw new ChainException("здоровье", new IOException("цепочка поднята, но CONNECT-roundtrip не прошёл"));
    }

    public async Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        Report(progress, "Прокси", ChainStepStatus.Active, "останавливаю");
        await proxy.StopAsync();
        Report(progress, "Прокси", ChainStepStatus.Done, "остановлен");

        Report(progress, "Узел", ChainStepStatus.Active, "tailscale down");
        try
        {
            await cli.DownAsync(ct);
            Report(progress, "Узел", ChainStepStatus.Done, "отключён");
        }
        catch (Exception ex)
        {
            Report(progress, "Узел", ChainStepStatus.Failed, ex.Message);
            log.Warn($"chain: down не удался: {ex.Message}");
        }

        Report(progress, "Демон", ChainStepStatus.Active, "останавливаю (нужны права)");
        try
        {
            await daemon.KillAsync(ct);
            Report(progress, "Демон", ChainStepStatus.Done, "остановлен");
        }
        catch (Exception ex)
        {
            Report(progress, "Демон", ChainStepStatus.Failed, ex.Message);
            throw new ChainException("демон", ex);
        }

        Report(progress, "Кипер", ChainStepStatus.Active, "останавливаю");
        await keeper.KillAsync(ct);
        Report(progress, "Кипер", ChainStepStatus.Done, "остановлен");
        log.Info("chain: остановлена");
    }

    private static void Report(IProgress<ChainStep>? progress, string component, ChainStepStatus status, string detail) =>
        progress?.Report(new ChainStep(component, status, detail));
}

/// <summary>Ошибка конкретного звена цепочки (для понятных сообщений UI/CLI).</summary>
public sealed class ChainException(string component, Exception inner) : Exception($"цепочка: {component}: {inner.Message}", inner);
