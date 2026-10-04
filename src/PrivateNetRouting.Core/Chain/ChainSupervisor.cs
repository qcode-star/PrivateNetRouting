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

/// <summary>Управление цепочкой (реализация — ChainSupervisor; интерфейс для лончера и тестов).</summary>
public interface IChainSupervisor
{
    Task<ChainState> GetStateAsync(CancellationToken ct);

    Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct);

    Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct);
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
    Func<CancellationToken, Task<bool>> healthCheck) : IChainSupervisor
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
            Report(progress, "Daemon", ChainStepStatus.Skipped, "already listening");
        }
        else
        {
            Report(progress, "Daemon", ChainStepStatus.Active, "starting (confirm UAC)");
            try
            {
                await daemon.EnsureStartedAsync(new Progress<string>(detail =>
                    Report(progress, "Daemon", ChainStepStatus.Active, detail)), ct);
                Report(progress, "Daemon", ChainStepStatus.Done, "listener is up");
            }
            catch (Exception ex)
            {
                Report(progress, "Daemon", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("daemon", ex);
            }
        }

        // 2. кипер watch-ipn
        if (keeper.IsRunning)
        {
            Report(progress, "Keeper", ChainStepStatus.Skipped, "already running");
        }
        else
        {
            Report(progress, "Keeper", ChainStepStatus.Active, "starting watch-ipn");
            try
            {
                await keeper.EnsureStartedAsync(ct);
                Report(progress, "Keeper", ChainStepStatus.Done, "running");
            }
            catch (Exception ex)
            {
                Report(progress, "Keeper", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("keeper", ex);
            }
        }

        // 3. узел (вход ключом из statedir, молчаливый)
        var state = await cli.GetBackendStateAsync(ct);
        if (state == "Running")
        {
            Report(progress, "Node", ChainStepStatus.Skipped, "already Running");
        }
        else
        {
            Report(progress, "Node", ChainStepStatus.Active, "tailscale up");
            try
            {
                var up = await cli.UpAsync(ct);
                state = await cli.GetBackendStateAsync(ct);
                if (state != "Running")
                {
                    throw new IOException($"up did not reach Running (now {state ?? "no response"}): {up.Output}");
                }

                Report(progress, "Node", ChainStepStatus.Done, "Running");
            }
            catch (Exception ex) when (ex is not ChainException)
            {
                Report(progress, "Node", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("node", ex);
            }
        }

        // 4. forward-прокси (наш порт ts-fwd)
        if (proxy.IsRunning)
        {
            Report(progress, "Proxy", ChainStepStatus.Skipped, "already running");
        }
        else
        {
            Report(progress, "Proxy", ChainStepStatus.Active, "starting listener");
            try
            {
                await proxy.StartAsync(ct);
                Report(progress, "Proxy", ChainStepStatus.Done, "listening");
            }
            catch (Exception ex) when (ex is not ChainException)
            {
                Report(progress, "Proxy", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("proxy", ex);
            }
        }

        // 5. здоровье: CONNECT-roundtrip через весь путь
        Report(progress, "Health", ChainStepStatus.Active, "CONNECT-roundtrip");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.HealthWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await healthCheck(ct))
            {
                Report(progress, "Health", ChainStepStatus.Done, "chain is alive");
                log.Info("chain: fully up");
                return;
            }

            log.Debug("chain: CONNECT-roundtrip not passing yet, retrying in 1 s");
            await Task.Delay(1000, ct);
        }

        Report(progress, "Health", ChainStepStatus.Failed, "CONNECT failed");
        throw new ChainException("health", new IOException("chain is up but CONNECT-roundtrip failed"));
    }

    public async Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        Report(progress, "Proxy", ChainStepStatus.Active, "stopping");
        await proxy.StopAsync();
        Report(progress, "Proxy", ChainStepStatus.Done, "stopped");

        Report(progress, "Node", ChainStepStatus.Active, "tailscale down");
        try
        {
            await cli.DownAsync(ct);
            Report(progress, "Node", ChainStepStatus.Done, "down");
        }
        catch (Exception ex)
        {
            Report(progress, "Node", ChainStepStatus.Failed, ex.Message);
            log.Warn($"chain: down failed: {ex.Message}");
        }

        Report(progress, "Daemon", ChainStepStatus.Active, "stopping (needs elevation)");
        try
        {
            await daemon.KillAsync(ct);
            Report(progress, "Daemon", ChainStepStatus.Done, "stopped");
        }
        catch (Exception ex)
        {
            Report(progress, "Daemon", ChainStepStatus.Failed, ex.Message);
            throw new ChainException("daemon", ex);
        }

        Report(progress, "Keeper", ChainStepStatus.Active, "stopping");
        await keeper.KillAsync(ct);
        Report(progress, "Keeper", ChainStepStatus.Done, "stopped");
        log.Info("chain: stopped");
    }

    /// <summary>Шаг в UI/CLI-прогресс; он же — Debug-строка в лог (полная картина подъёма).</summary>
    private void Report(IProgress<ChainStep>? progress, string component, ChainStepStatus status, string detail)
    {
        log.Debug($"chain: {component}: {status.ToString().ToLowerInvariant()}: {detail}");
        progress?.Report(new ChainStep(component, status, detail));
    }
}

/// <summary>Ошибка конкретного звена цепочки (для понятных сообщений UI/CLI).</summary>
public sealed class ChainException(string component, Exception inner) : Exception($"chain: {component}: {inner.Message}", inner);
