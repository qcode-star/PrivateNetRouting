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

/// <summary>
/// Snapshot of all chain links (read-only). In the socks5 mode the daemon/keeper/node fields stay
/// neutral and UpstreamReachable carries the egress state; null — the tailscale mode, where the
/// link trio speaks through those fields.
/// </summary>
public sealed record ChainState(
    bool DaemonHttpListening,
    bool DaemonProcessAlive,
    bool KeeperRunning,
    string? BackendState,
    bool ProxyRunning,
    bool ProxyHealthy,
    bool? UpstreamReachable = null)
{
    public bool IsFullyUp =>
        ProxyRunning
        && ProxyHealthy
        && (UpstreamReachable.HasValue
            ? UpstreamReachable.Value
            : DaemonHttpListening && DaemonProcessAlive && KeeperRunning && BackendState == "Running");

    /// <summary>Nothing is up at all — the chain's normal resting state, not a partial one.</summary>
    public bool IsStopped =>
        !ProxyRunning
        && !DaemonProcessAlive
        && !DaemonHttpListening
        && !KeeperRunning
        && BackendState != "Running"
        && UpstreamReachable != true;
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

    /// <summary>
    /// Idempotent bring-up. Honours <paramref name="ct"/>: a cancellation surfaces as
    /// OperationCanceledException (never wrapped in ChainException), so the caller can tell
    /// "the stop button cancelled the bring-up" from a link failure.
    /// </summary>
    Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct);

    Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct);
}

/// <summary>
/// Chain orchestrator: idempotent bring-up and full shutdown. The mode picks the first stages:
/// tailscale — daemon → keeper → node (liveness: daemon HTTP listener, watch-ipn process,
/// BackendState=Running), socks5 — a single check of the provided external upstream. Both
/// continue proxy → health (proxy listener, live CONNECT-roundtrip). A successful bring-up ends
/// with the chainRaised hook — the idle stopper re-arms itself through it (watchdog.autoStart);
/// a completed stop ends with the chainStopped hook — the idle stopper is taken down through it
/// (a stopped chain has nothing left to stop).
/// </summary>
public sealed class ChainSupervisor(
    ChainConfig config,
    IProxyControl proxy,
    ISidecarDaemon daemon,
    ISidecarKeeper keeper,
    ISidecarCli cli,
    FileLog log,
    Func<CancellationToken, Task<bool>> healthCheck,
    Action? chainRaised = null,
    Func<CancellationToken, Task>? chainStopped = null,
    Func<CancellationToken, Task<bool>>? upstreamCheck = null) : IChainSupervisor
{
    private readonly bool _socks5 = config.EffectiveMode == ChainMode.Socks5;

    // the socks5 mode needs the check; without it everything honestly reports "not reachable"
    private readonly Func<CancellationToken, Task<bool>> _upstreamCheck =
        upstreamCheck ?? (_ => Task.FromResult(false));

    public async Task<ChainState> GetStateAsync(CancellationToken ct)
    {
        if (_socks5)
        {
            var socksProxyRunning = proxy.IsRunning;
            var socksProxyHealthy = socksProxyRunning && await healthCheck(ct);
            return new ChainState(
                DaemonHttpListening: false,
                DaemonProcessAlive: false,
                KeeperRunning: false,
                BackendState: null,
                ProxyRunning: socksProxyRunning,
                ProxyHealthy: socksProxyHealthy,
                UpstreamReachable: await _upstreamCheck(ct));
        }

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
        if (_socks5)
        {
            await EnsureEgressAsync(progress, ct);
        }
        else
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
                catch (Exception ex) when (ex is not OperationCanceledException)
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
                catch (Exception ex) when (ex is not OperationCanceledException)
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
                catch (Exception ex) when (ex is not ChainException and not OperationCanceledException)
                {
                    Report(progress, "Node", ChainStepStatus.Failed, ex.Message);
                    throw new ChainException("node", ex);
                }
            }
        }

        // forward proxy
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
            catch (Exception ex) when (ex is not ChainException and not OperationCanceledException)
            {
                Report(progress, "Proxy", ChainStepStatus.Failed, ex.Message);
                throw new ChainException("proxy", ex);
            }
        }

        // health: CONNECT-roundtrip through the whole path; every attempt is capped by the
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

    /// <summary>
    /// socks5 egress: the provided external upstream must carry a live CONNECT before anything
    /// else comes up. Polls like the health phase, so a slow upstream ends at the deadline.
    /// </summary>
    private async Task EnsureEgressAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        Report(progress, "Egress", ChainStepStatus.Active, "checking egress");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.HealthWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = deadline - DateTime.UtcNow;
            attemptCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            if (await _upstreamCheck(attemptCts.Token))
            {
                Report(progress, "Egress", ChainStepStatus.Done, "egress is alive");
                return;
            }

            log.Debug("chain: egress check not passing yet, retrying in 1 s");
            await Task.Delay(1000, ct);
        }

        Report(progress, "Egress", ChainStepStatus.Failed, "egress check failed");
        throw new ChainException("egress", new IOException("the provided SOCKS5 upstream does not pass the CONNECT check"));
    }

    public async Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        Report(progress, "Proxy", ChainStepStatus.Active, "stopping");
        await proxy.StopAsync();
        Report(progress, "Proxy", ChainStepStatus.Done, "stopped");

        // socks5 egress: the external upstream is not ours — nothing to bring down past the proxy
        if (!_socks5)
        {
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
        }

        log.Info("chain: stopped");

        // the idle stopper exists only over a live chain: the stop is its end too. Its own token
        // is not trusted here — a cancelled chain stop must still take the stoppers down
        if (chainStopped is not null)
        {
            await chainStopped(CancellationToken.None);
        }
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
