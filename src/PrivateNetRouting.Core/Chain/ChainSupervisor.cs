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

/// <summary>Bring-up/shutdown step for UI and CLI: component, status, detail; NeedsUac — the step goes through the elevated helper, a UAC prompt is possible.</summary>
public sealed record ChainStep(string Component, ChainStepStatus Status, string Detail, bool NeedsUac = false);

/// <summary>Snapshot of all chain links (read-only).</summary>
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

/// <summary>Proxy control on the orchestrator side (implementation — ForwardProxyService).</summary>
public interface IProxyControl
{
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}

/// <summary>Chain control (implementation — ChainSupervisor; interface for the launcher and tests).</summary>
public interface IChainSupervisor
{
    Task<ChainState> GetStateAsync(CancellationToken ct);

    Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct);

    Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct);
}

/// <summary>
/// Chain orchestrator: idempotent bring-up (daemon → keeper → node → proxy → health)
/// and full shutdown (down → daemon → keeper → proxy). Order and liveness signals:
/// daemon HTTP listener, watch-ipn process, BackendState=Running,
/// proxy listener, live CONNECT-roundtrip. A successful bring-up ends with the
/// chainRaised hook — the idle stopper re-arms itself through it (watchdog.autoStart).
/// </summary>
public sealed class ChainSupervisor(
    ChainConfig config,
    IProxyControl proxy,
    ISidecarDaemon daemon,
    ISidecarKeeper keeper,
    ISidecarCli cli,
    FileLog log,
    Func<CancellationToken, Task<bool>> healthCheck,
    Action? chainRaised = null) : IChainSupervisor
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
        // 1. daemon (UAC only in the elevated mode: chain.daemonElevation=always
        // or an old tailscale under auto)
        if (daemon.IsHttpListening && daemon.IsProcessAlive)
        {
            Report(progress, "Daemon", ChainStepStatus.Skipped, "already listening");
        }
        else
        {
            var needsUac = daemon.StartNeedsUac();
            Report(progress, "Daemon", ChainStepStatus.Active, needsUac ? "starting (confirm UAC)" : "starting", needsUac);
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

        // 2. watch-ipn keeper
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

        // 3. node (silent login by key from statedir)
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

        // 4. forward proxy
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

        // 5. health: CONNECT-roundtrip through the whole path; every attempt is capped by the
        // remaining budget, so the phase ends at the deadline instead of a probe-timeout past it
        Report(progress, "Health", ChainStepStatus.Active, "CONNECT-roundtrip");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.HealthWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = deadline - DateTime.UtcNow;
            attemptCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            if (await healthCheck(attemptCts.Token))
            {
                Report(progress, "Health", ChainStepStatus.Done, "chain is alive");
                log.Info("chain: fully up");
                chainRaised?.Invoke();
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

        Report(progress, "Daemon", ChainStepStatus.Active, "stopping");
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

    /// <summary>A step into the UI/CLI progress; also a Debug line to the log (the full bring-up picture).</summary>
    private void Report(IProgress<ChainStep>? progress, string component, ChainStepStatus status, string detail, bool needsUac = false)
    {
        log.Debug($"chain: {component}: {status.ToString().ToLowerInvariant()}: {detail}");
        progress?.Report(new ChainStep(component, status, detail, needsUac));
    }
}

/// <summary>Error of a specific chain link (for clear UI/CLI messages).</summary>
public sealed class ChainException(string component, Exception inner) : Exception($"chain: {component}: {inner.Message}", inner);
