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

/// <summary>
/// Bring-up/shutdown step for UI and CLI: component, status, detail; NeedsUac — the step goes
/// through the elevated helper, a UAC prompt is possible; AuthUrl — the tailnet authentication
/// URL of the interactive node login (the GUI opens it in the browser, the CLI prints it).
/// </summary>
public sealed record ChainStep(
    string Component,
    ChainStepStatus Status,
    string Detail,
    bool NeedsUac = false,
    string? AuthUrl = null);

/// <summary>
/// Snapshot of all chain links (read-only). In the external-upstream modes (socks5, http) the
/// daemon/keeper/node fields stay
/// neutral and UpstreamReachable carries the egress state; null — the tailscale mode, where the
/// link trio speaks through those fields. ProxyRunning is null when there is no local proxy in
/// this shape at all (socks5 + forwardProxy.enabled=false, or the http mode): the applications
/// point at the external endpoint and "is our proxy running" is not a meaningful question.
/// </summary>
public sealed record ChainState(
    bool DaemonHttpListening,
    bool DaemonProcessAlive,
    bool KeeperRunning,
    string? BackendState,
    bool? ProxyRunning,
    bool ProxyHealthy,
    bool? UpstreamReachable = null)
{
    public bool IsFullyUp =>
        ProxyRunning != false
        && ProxyHealthy
        && (UpstreamReachable.HasValue
            ? UpstreamReachable.Value
            : DaemonHttpListening && DaemonProcessAlive && KeeperRunning && BackendState == "Running");

    /// <summary>Nothing is up at all — the chain's normal resting state, not a partial one.</summary>
    public bool IsStopped =>
        ProxyRunning != true
        && !DaemonProcessAlive
        && !DaemonHttpListening
        && !KeeperRunning
        && BackendState != "Running"
        && UpstreamReachable != true;
}

/// <summary>
/// The links one bring-up started itself, recorded by the orchestrator as it starts them (under
/// its operation lock, so a concurrent bring-up of another caller is never attributed here). A
/// link is recorded before its start is attempted: a start cancelled or failed half-way may
/// still have left a process behind, and a stale daemon that was killed and started anew counts
/// as started. The rollback of a cancelled launch stops exactly these links.
/// </summary>
public sealed class ChainRaisedLinks
{
    private volatile bool _daemon;
    private volatile bool _keeper;
    private volatile bool _node;
    private volatile bool _proxy;

    public bool Daemon => _daemon;

    public bool Keeper => _keeper;

    /// <summary>The node was brought up (`tailscale up`, possibly after a login) — its rollback is `tailscale down`.</summary>
    public bool Node => _node;

    public bool Proxy => _proxy;

    public bool Any => Daemon || Keeper || Node || Proxy;

    internal void MarkDaemon() => _daemon = true;

    internal void MarkKeeper() => _keeper = true;

    internal void MarkNode() => _node = true;

    internal void MarkProxy() => _proxy = true;
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

    /// <summary>
    /// The bring-up that records into <paramref name="raised"/> every link it starts itself (null —
    /// nothing is recorded). The default forwards to the plain bring-up.
    /// </summary>
    Task EnsureRunningAsync(IProgress<ChainStep>? progress, ChainRaisedLinks? raised, CancellationToken ct) =>
        EnsureRunningAsync(progress, ct);

    Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct);

    /// <summary>
    /// Stops only the links recorded in <paramref name="raised"/> by a bring-up — a cancelled
    /// launch extinguishes what it raised itself and leaves the links that already existed alone.
    /// The default is the full stop.
    /// </summary>
    Task StopRaisedAsync(ChainRaisedLinks raised, IProgress<ChainStep>? progress, CancellationToken ct) =>
        StopAsync(progress, ct);
}

/// <summary>
/// Chain orchestrator: idempotent bring-up and full shutdown. The mode picks the first stages:
/// tailscale — daemon → keeper → node (liveness: daemon HTTP listener, watch-ipn process,
/// BackendState=Running), socks5/http — a single check of the provided external upstream. Both
/// continue proxy → health (proxy listener, live CONNECT-roundtrip) — except the shapes with the
/// local proxy skipped (socks5 + forwardProxy.enabled=false, and the http mode always), where
/// there is no proxy link and health probes the external endpoint straight away. The tailscale
/// mode with the proxy disabled is a config error: the proxy is load-bearing there (DoH
/// resolution, dialing by IP), the bring-up refuses with an explicit message. A successful
/// bring-up ends with the chainRaised hook — the idle stopper re-arms itself through it
/// (watchdog.autoStart); a completed stop ends with the chainStopped hook — the idle stopper is
/// taken down through it (a stopped chain has nothing left to stop).
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
    Func<CancellationToken, Task<bool>>? upstreamCheck = null,
    Func<CancellationToken, Task<string?>>? systemControlUrl = null,
    bool proxyDisabled = false) : IChainSupervisor
{
    /// <summary>The external-upstream modes (socks5, http): no daemon/keeper/node links exist there.</summary>
    private readonly bool _external = config.EffectiveMode is ChainMode.Socks5 or ChainMode.Http;

    /// <summary>No local proxy in this shape: bring-up stops after the egress check, health probes the external endpoint.
    /// The http mode never runs the local proxy; in the socks5 mode the skip follows forwardProxy.enabled=false.</summary>
    private readonly bool _skipProxy =
        config.EffectiveMode == ChainMode.Http || (proxyDisabled && config.EffectiveMode == ChainMode.Socks5);

    /// <summary>The tailscale mode cannot work without the local proxy — the bring-up refuses.</summary>
    private readonly bool _proxyDisabledInTailscale = proxyDisabled && config.EffectiveMode == ChainMode.Tailscale;

    // the external modes need the check; without it everything honestly reports "not reachable"
    private readonly Func<CancellationToken, Task<bool>> _upstreamCheck =
        upstreamCheck ?? (_ => Task.FromResult(false));

    // the first node login picks the control server from it (the machine's own tailnet); without
    // a finder (tests) the login goes to the default control server
    private readonly Func<CancellationToken, Task<string?>> _systemControlUrl =
        systemControlUrl ?? (_ => Task.FromResult<string?>(null));

    /// <summary>
    /// The state snapshot runs on the thread pool: it connects to the daemon pipe (a blocking
    /// connect that spins for ~500 ms when the daemon is down), reads the WMI process list and
    /// spawns the CLI - none of it may run on a caller's UI thread, and the awaits inside must
    /// not capture its context.
    /// </summary>
    public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.Run(() => GetStateCoreAsync(ct));

    private async Task<ChainState> GetStateCoreAsync(CancellationToken ct)
    {
        if (_external)
        {
            if (_skipProxy)
            {
                var reachable = await _upstreamCheck(ct);
                var healthy = reachable && await healthCheck(ct);
                return new ChainState(
                    DaemonHttpListening: false,
                    DaemonProcessAlive: false,
                    KeeperRunning: false,
                    BackendState: null,
                    ProxyRunning: null,
                    ProxyHealthy: healthy,
                    UpstreamReachable: reachable);
            }

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

    /// <summary>
    /// One chain operation at a time per process: a bring-up and a stop (or two bring-ups, from the
    /// card, a launch and an update window) must not interleave - a second bring-up would see the
    /// first one's daemon "alive without listener" and kill it, and a stop during a pending UAC
    /// prompt would find nothing to stop. A waiter simply runs its own idempotent pass afterwards.
    /// </summary>
    private readonly SemaphoreSlim _operation = new(1, 1);

    /// <summary>
    /// Bring-up and stop hop to the thread pool before touching anything: the synchronous prefix
    /// (elevation probe, pipe connect, WMI) must not run on a UI thread, and the awaits inside
    /// must not capture its context (a blocking wait on that thread would deadlock them).
    /// </summary>
    public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct) =>
        EnsureRunningAsync(progress, null, ct);

    public Task EnsureRunningAsync(IProgress<ChainStep>? progress, ChainRaisedLinks? raised, CancellationToken ct) =>
        Task.Run(() => EnsureRunningSerializedAsync(progress, raised, ct));

    private async Task EnsureRunningSerializedAsync(IProgress<ChainStep>? progress, ChainRaisedLinks? raised, CancellationToken ct)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureRunningCoreAsync(progress, raised, ct).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    public Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct) =>
        Task.Run(() => StopSerializedAsync(progress, ct));

    public Task StopRaisedAsync(ChainRaisedLinks raised, IProgress<ChainStep>? progress, CancellationToken ct) =>
        Task.Run(() => StopSerializedAsync(progress, ct, raised));

    private async Task StopSerializedAsync(IProgress<ChainStep>? progress, CancellationToken ct, ChainRaisedLinks? raised = null)
    {
        // a stop is never abandoned while it waits for its turn (the steps run to the end even on
        // a cancelled token): the bring-up in flight is cancelled by its own owner and lets go
        await _operation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(progress, ct, raised).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task EnsureRunningCoreAsync(IProgress<ChainStep>? progress, ChainRaisedLinks? raised, CancellationToken ct)
    {
        if (_proxyDisabledInTailscale)
        {
            Report(progress, "Proxy", ChainStepStatus.Failed,
                "the local proxy is disabled (forwardProxy.enabled=false) but required in the tailscale mode");
            throw new ChainException("proxy", new IOException(
                "the local proxy is disabled (forwardProxy.enabled=false) but required in the tailscale mode: it does the DoH resolution and the dialing by IP"));
        }

        if (_external)
        {
            await EnsureEgressAsync(progress, ct);
        }
        else
        {
            // 1. daemon (UAC only in the elevated mode: chain.tailscale.elevation=always
            // or an old tailscale under auto)
            if (daemon.IsHttpListening && daemon.IsProcessAlive)
            {
                Report(progress, "Daemon", ChainStepStatus.Skipped, "already listening");
            }
            else
            {
                var needsUac = daemon.StartNeedsUac();
                Report(progress, "Daemon", ChainStepStatus.Active, needsUac ? "starting (confirm UAC)" : "starting", needsUac);
                raised?.MarkDaemon(); // a stale daemon killed and started anew is ours to roll back too
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
                raised?.MarkKeeper();
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

            // 3. node (silent when the statedir holds a logged-in key; a node without one goes
            // through the interactive login first — the auth URL rides the progress, the GUI
            // opens it in the browser, the CLI prints it)
            var state = await cli.GetBackendStateAsync(ct);
            if (state == "Running")
            {
                Report(progress, "Node", ChainStepStatus.Skipped, "already Running");
            }
            else
            {
                Report(progress, "Node", ChainStepStatus.Active, "tailscale up");
                raised?.MarkNode();
                try
                {
                    if (state is "NoState" or "NeedsLogin")
                    {
                        await LoginNodeAsync(progress, ct);
                    }

                    var up = await cli.UpAsync(await ResolveUpControlUrlAsync(ct), ct);
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

        // forward proxy — unless the skipped shape (socks5 + forwardProxy.enabled=false, or the
        // http mode): the applications point at the external endpoint and there is no local
        // listener to start (the rail has no Proxy stage either)
        if (!_skipProxy)
        {
            if (proxy.IsRunning)
            {
                Report(progress, "Proxy", ChainStepStatus.Skipped, "already running");
            }
            else
            {
                Report(progress, "Proxy", ChainStepStatus.Active, "starting listener");
                raised?.MarkProxy();
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
    /// External-upstream egress (socks5/http): the provided upstream must carry a live CONNECT
    /// before anything else comes up. Polls like the health phase, so a slow upstream ends at
    /// the deadline.
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
            bool alive;
            try
            {
                alive = await _upstreamCheck(attemptCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the attempt's own deadline (or the check's internal one) fired while the caller
                // still waits: "not reachable yet", never the caller's cancellation
                alive = false;
            }

            if (alive)
            {
                Report(progress, "Egress", ChainStepStatus.Done, "egress is alive");
                return;
            }

            log.Debug("chain: egress check not passing yet, retrying in 1 s");
            await Task.Delay(1000, ct);
        }

        Report(progress, "Egress", ChainStepStatus.Failed, "egress check failed");
        var kind = config.EffectiveMode == ChainMode.Http ? "HTTP" : "SOCKS5";
        throw new ChainException("egress", new IOException($"the provided {kind} upstream does not pass the CONNECT check"));
    }

    /// <summary>
    /// raised - the links the bring-up being rolled back started itself: only those are stopped
    /// (null - every link). The chain-stopped hook runs only when nothing of ours is left running
    /// afterwards, because the idle stopper belongs to a chain that is still partly up.
    /// </summary>
    private async Task StopCoreAsync(IProgress<ChainStep>? progress, CancellationToken ct, ChainRaisedLinks? raised = null)
    {
        var stopProxy = raised is null || raised.Proxy;
        var stopNode = raised is null || raised.Node;
        var stopDaemon = raised is null || raised.Daemon;
        var stopKeeper = raised is null || raised.Keeper;

        // every step runs even when an earlier one failed: a daemon that refuses to die must not
        // leave the keeper (and a still-listening proxy) behind; the failures are raised at the end.
        // The stop links run with no token on purpose: a stop runs to the end even on a cancelled
        // token (a cancelled stop must still leave no daemon and no idle stoppers), so the links
        // must not observe the caller's cancellation — only the wait for the semaphore above does.
        var failures = new List<(string Component, Exception Error)>();

        // the skipped shape has no local proxy to stop; the external upstream is not ours
        if (!_skipProxy && stopProxy)
        {
            Report(progress, "Proxy", ChainStepStatus.Active, "stopping");
            try
            {
                await proxy.StopAsync();
                Report(progress, "Proxy", ChainStepStatus.Done, "stopped");
            }
            catch (Exception ex)
            {
                Report(progress, "Proxy", ChainStepStatus.Failed, ex.Message);
                failures.Add(("proxy", ex));
            }
        }

        // external-upstream egress: the external upstream is not ours — nothing to bring down past the proxy
        if (!_external)
        {
            if (stopNode)
            {
                Report(progress, "Node", ChainStepStatus.Active, "tailscale down");
                try
                {
                    await cli.DownAsync(CancellationToken.None);
                    Report(progress, "Node", ChainStepStatus.Done, "down");
                }
                catch (Exception ex)
                {
                    Report(progress, "Node", ChainStepStatus.Failed, ex.Message);
                    log.Warn($"chain: down failed: {ex.Message}");
                }
            }

            if (stopDaemon)
            {
                Report(progress, "Daemon", ChainStepStatus.Active, "stopping");
                try
                {
                    await daemon.KillAsync(CancellationToken.None);
                    Report(progress, "Daemon", ChainStepStatus.Done, "stopped");
                }
                catch (Exception ex)
                {
                    Report(progress, "Daemon", ChainStepStatus.Failed, ex.Message);
                    failures.Add(("daemon", ex));
                }
            }

            if (stopKeeper)
            {
                Report(progress, "Keeper", ChainStepStatus.Active, "stopping");
                try
                {
                    await keeper.KillAsync(CancellationToken.None);
                    Report(progress, "Keeper", ChainStepStatus.Done, "stopped");
                }
                catch (Exception ex)
                {
                    Report(progress, "Keeper", ChainStepStatus.Failed, ex.Message);
                    failures.Add(("keeper", ex));
                }
            }
        }

        if (failures.Count > 0)
        {
            foreach (var (component, error) in failures.Skip(1))
            {
                log.Warn($"chain: stop: {component} failed too: {error.Message}");
            }

            throw new ChainException(failures[0].Component, failures[0].Error);
        }

        // a rollback leaves the links that were up before the bring-up; it ends the whole chain
        // only when nothing of ours is running any more
        var wholeChain = raised is null || NothingOfOursRunning();
        log.Info(wholeChain ? "chain: stopped" : "chain: stopped the links this operation raised, the links that were up before are left running");

        // the idle stopper exists only over a live chain: the stop is its end too. Its own token
        // is not trusted here — a cancelled chain stop must still take the stoppers down
        if (wholeChain && chainStopped is not null)
        {
            await chainStopped(CancellationToken.None);
        }
    }

    /// <summary>The local links of this shape are all down (the external upstream is not ours).</summary>
    private bool NothingOfOursRunning() =>
        (_skipProxy || !proxy.IsRunning)
        && (_external || (!keeper.IsRunning && !daemon.IsProcessAlive && !daemon.IsHttpListening));

    /// <summary>
    /// The interactive login of a node that has none: `tailscale login` waits while the user
    /// finishes the authentication in the browser; the auth URL rides the progress as soon as it
    /// appears. The control server: chain.tailscale.loginServer, or — when empty — the one the
    /// machine's system-wide tailscale uses (read from its prefs; a silent system daemon leaves
    /// the default). The node key lands in the statedir — the login is one-time.
    /// </summary>
    private async Task LoginNodeAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        var loginServer = !string.IsNullOrWhiteSpace(config.Tailscale.LoginServer)
            ? config.Tailscale.LoginServer
            : await _systemControlUrl(ct);

        Report(progress, "Node", ChainStepStatus.Active, "node is not logged in — waiting for authentication");
        var login = await cli.LoginAsync(loginServer, url => Report(
            progress, "Node", ChainStepStatus.Active,
            $"open this address to authenticate: {url}",
            authUrl: url), ct);
        if (!login.Ok)
        {
            throw new IOException($"login failed: {login.Output}");
        }

        var state = await cli.GetBackendStateAsync(ct);
        if (state == "NeedsMachineAuth")
        {
            Report(progress, "Node", ChainStepStatus.Active,
                "authenticated — waiting for the tailnet's device approval");
        }
    }

    /// <summary>
    /// The control server `up` has to mention: tailscale rejects an up that leaves out a
    /// non-default control server the node is bound to ("requires mentioning all non-default
    /// flags"). chain.tailscale.loginServer when set; otherwise the node's own current ControlURL
    /// (the login may have picked the system tailscale's one), and — when the node does not say —
    /// the system tailscale's control server, the same guess the login makes.
    /// </summary>
    private async Task<string?> ResolveUpControlUrlAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.Tailscale.LoginServer))
        {
            return config.Tailscale.LoginServer;
        }

        return await cli.GetControlUrlAsync(ct) ?? await _systemControlUrl(ct);
    }

    /// <summary>A step into the UI/CLI progress; also a Debug line to the log (the full bring-up picture).</summary>
    private void Report(IProgress<ChainStep>? progress, string component, ChainStepStatus status, string detail, bool needsUac = false, string? authUrl = null)
    {
        log.Debug($"chain: {component}: {status.ToString().ToLowerInvariant()}: {detail}");
        progress?.Report(new ChainStep(component, status, detail, needsUac, authUrl));
    }
}

/// <summary>Error of a specific chain link (for clear UI/CLI messages).</summary>
public sealed class ChainException(string component, Exception inner) : Exception($"chain: {component}: {inner.Message}", inner);
