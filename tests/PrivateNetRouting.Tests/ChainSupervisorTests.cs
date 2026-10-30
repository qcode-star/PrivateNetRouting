using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>Chain orchestration on fakes: order, idempotence, link failures.</summary>
[Collection(nameof(ElevatedHelperPinnedRoot))]
public class ChainSupervisorTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-chain-{Guid.NewGuid():N}.log");
    private readonly List<string> _calls = [];
    private readonly List<FileLog> _logs = [];

    private ChainSupervisor Create(
        FakeProxy? proxy = null,
        FakeDaemon? daemon = null,
        FakeKeeper? keeper = null,
        FakeCli? cli = null,
        Func<CancellationToken, Task<bool>>? health = null,
        Action? chainRaised = null,
        int healthWaitSeconds = 5,
        string? mode = null,
        Func<CancellationToken, Task>? chainStopped = null,
        Func<CancellationToken, Task<bool>>? upstream = null,
        Func<CancellationToken, Task<string?>>? systemControlUrl = null,
        string? loginServer = null,
        bool proxyDisabled = false)
    {
        var log = new FileLog(_logPath);
        _logs.Add(log);
        return new ChainSupervisor(
            new ChainConfig
            {
                HealthWaitSeconds = healthWaitSeconds,
                Mode = mode ?? "tailscale",
                Tailscale = new ChainTailscaleConfig { LoginServer = loginServer ?? "" },
            },
            proxy ?? new FakeProxy(_calls),
            daemon ?? new FakeDaemon(_calls),
            keeper ?? new FakeKeeper(_calls),
            cli ?? new FakeCli(_calls),
            log,
            health ?? (_ => Task.FromResult(true)),
            chainRaised,
            chainStopped,
            upstream,
            systemControlUrl,
            proxyDisabled);
    }

    [Fact]
    public async Task FullyAliveChainIsSkipped()
    {
        var daemon = new FakeDaemon(_calls) { IsHttpListening = true, IsProcessAlive = true };
        var keeper = new FakeKeeper(_calls) { IsRunning = true };
        var cli = new FakeCli(_calls) { BackendState = "Running" };
        var proxy = new FakeProxy(_calls) { IsRunning = true };
        var supervisor = Create(proxy, daemon, keeper, cli);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Empty(_calls); // no action at all, everything is skipped
        var state = await supervisor.GetStateAsync(CancellationToken.None);
        Assert.True(state.IsFullyUp);
    }

#pragma warning disable xUnit1031 // the point of the test is a blocking wait on a "UI thread"
    [Fact]
    public void ChainOperationsDoNotRunOrCaptureOnTheCallersSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        var trapped = new TrappedContext();
        SynchronizationContext.SetSynchronizationContext(trapped);
        try
        {
            var callerThread = Environment.CurrentManagedThreadId;
            var healthThreads = new List<int>();
            var healthContexts = new List<SynchronizationContext?>();
            var supervisor = Create(
                proxy: new FakeProxy(_calls) { IsRunning = true },
                health: _ =>
                {
                    healthThreads.Add(Environment.CurrentManagedThreadId);
                    healthContexts.Add(SynchronizationContext.Current);
                    return Task.FromResult(true);
                });

            // blocking waits on the "UI thread": a continuation posted to the trapped context would deadlock them
            Assert.True(supervisor.GetStateAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10)), "GetStateAsync deadlocked");
            Assert.True(supervisor.EnsureRunningAsync(null, CancellationToken.None).Wait(TimeSpan.FromSeconds(10)), "EnsureRunningAsync deadlocked");
            Assert.True(supervisor.StopAsync(null, CancellationToken.None).Wait(TimeSpan.FromSeconds(10)), "StopAsync deadlocked");

            Assert.NotEmpty(healthThreads);
            Assert.All(healthThreads, id => Assert.NotEqual(callerThread, id));
            Assert.All(healthContexts, Assert.Null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
#pragma warning restore xUnit1031

    /// <summary>"Blocked dispatcher" context: Post is swallowed, so any captured continuation never runs.</summary>
    private sealed class TrappedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    [Fact]
    public async Task ColdChainStateIsStoppedNotPartial()
    {
        var supervisor = Create();

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.False(state.IsFullyUp);
        Assert.True(state.IsStopped); // nothing is up — "stopped", not "partial"
    }

    [Fact]
    public async Task PartiallyUpChainIsNeitherFullyUpNorStopped()
    {
        var daemon = new FakeDaemon(_calls) { IsHttpListening = true, IsProcessAlive = true };
        var supervisor = Create(daemon: daemon);

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.False(state.IsFullyUp);
        Assert.False(state.IsStopped); // the daemon alone is up
    }

    [Fact]
    public void Socks5StoppedChainIsStoppedEvenWithUntestedUpstream()
    {
        // the external upstream was never checked (null) and the proxy is down — stopped
        var state = new ChainState(
            DaemonHttpListening: false,
            DaemonProcessAlive: false,
            KeeperRunning: false,
            BackendState: null,
            ProxyRunning: false,
            ProxyHealthy: false,
            UpstreamReachable: null);
        Assert.True(state.IsStopped);

        var reachable = state with { UpstreamReachable = true };
        Assert.False(reachable.IsStopped); // a reachable upstream alone is "partial", the stop button keys off it
    }

    [Fact]
    public async Task ColdStartRunsInOrder()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin" };
        var supervisor = Create(cli: cli);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Equal(
            ["daemon.ensure", "keeper.ensure", "cli.login", "cli.up", "proxy.start"],
            _calls);
        Assert.Equal("Running", cli.BackendState); // after up the node must become Running
    }

    [Fact]
    public async Task NeedsLoginRunsInteractiveLoginBeforeUp()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin", AuthUrl = "https://login.example/a/0123456789abcdef" };
        var collector = new StepCollector();
        var supervisor = Create(cli: cli);

        await supervisor.EnsureRunningAsync(collector, CancellationToken.None);

        Assert.Equal(
            ["daemon.ensure", "keeper.ensure", "cli.login", "cli.up", "proxy.start"],
            _calls);
        var loginStep = collector.Steps.Single(s => s.Component == "Node" && s.AuthUrl is not null);
        Assert.Equal(ChainStepStatus.Active, loginStep.Status);
        Assert.Contains("https://login.example/a/0123456789abcdef", loginStep.Detail);
    }

    [Fact]
    public async Task FreshNodeWithoutStateLogsInToo()
    {
        var cli = new FakeCli(_calls) { BackendState = "NoState" };
        var supervisor = Create(cli: cli);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Contains("cli.login", _calls);
        Assert.Contains("cli.up", _calls);
    }

    [Fact]
    public async Task DownedNodeWithAKeySkipsTheLogin()
    {
        var cli = new FakeCli(_calls) { BackendState = "Stopped" }; // downed earlier, the key is intact
        var supervisor = Create(cli: cli);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.DoesNotContain("cli.login", _calls);
        Assert.Contains("cli.up", _calls);
    }

    [Fact]
    public async Task ConfiguredLoginServerWinsOverTheSystemOne()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin" };
        var supervisor = Create(
            cli: cli,
            systemControlUrl: _ => Task.FromResult<string?>("https://system.example/control"),
            loginServer: "https://config.example/control");

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Contains("cli.login:https://config.example/control", _calls);
        Assert.Equal("https://config.example/control", cli.LastLoginServer);
    }

    [Fact]
    public async Task LoginServerIsStolenFromTheSystemTailscale()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin" };
        var lookups = 0;
        var supervisor = Create(
            cli: cli,
            systemControlUrl: _ =>
            {
                lookups++;
                return Task.FromResult<string?>("https://system.example/control");
            });

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Equal(1, lookups); // only the login needs it — up takes the server from the node state
        Assert.Contains("cli.login:https://system.example/control", _calls);
        Assert.Equal("https://system.example/control", cli.LastUpServer); // up mentions the server the login bound
    }

    /// <summary>
    /// A downed node bound to a custom control server: `up` must mention it, or tailscale rejects
    /// the up ("requires mentioning all non-default flags"). The node's own prefs win over the
    /// system tailscale's guess.
    /// </summary>
    [Fact]
    public async Task UpMentionsTheControlServerTheNodeIsBoundTo()
    {
        var cli = new FakeCli(_calls) { BackendState = "Stopped", ControlUrl = "https://node.example/control" };
        var supervisor = Create(
            cli: cli,
            systemControlUrl: _ => Task.FromResult<string?>("https://system.example/control"));

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.DoesNotContain(_calls, c => c.StartsWith("cli.login", StringComparison.Ordinal));
        Assert.Equal("https://node.example/control", cli.LastUpServer);
    }

    [Fact]
    public async Task UpFallsBackToTheSystemControlServerWhenTheNodeIsSilent()
    {
        var cli = new FakeCli(_calls) { BackendState = "Stopped" };
        var supervisor = Create(
            cli: cli,
            systemControlUrl: _ => Task.FromResult<string?>("https://system.example/control"));

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Equal("https://system.example/control", cli.LastUpServer);
    }

    [Fact]
    public async Task UpMentionsTheConfiguredLoginServer()
    {
        var cli = new FakeCli(_calls) { BackendState = "Stopped", ControlUrl = "https://node.example/control" };
        var supervisor = Create(cli: cli, loginServer: "https://config.example/control");

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Equal("https://config.example/control", cli.LastUpServer);
    }

    [Fact]
    public async Task UpWithoutAnyKnownServerLeavesTheDefault()
    {
        var cli = new FakeCli(_calls) { BackendState = "Stopped" };
        var supervisor = Create(cli: cli);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Contains("cli.up", _calls);
        Assert.Null(cli.LastUpServer);
    }

    [Fact]
    public async Task SilentSystemTailscaleLeavesTheDefaultControlServer()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin" };
        var supervisor = Create(cli: cli, systemControlUrl: _ => Task.FromResult<string?>(null));

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Contains("cli.login", _calls); // no server suffix — the default control server
    }

    [Fact]
    public async Task LoginFailureFailsTheNodeStepBeforeUp()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin", LoginThrows = new IOException("login timed out") };
        var supervisor = Create(cli: cli);

        var ex = await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.Contains("node", ex.Message);
        Assert.DoesNotContain("cli.up", _calls);
    }

    [Fact]
    public async Task SuccessfulRaiseFiresTheHookAndFailureDoesNot()
    {
        var raised = new List<int>();
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin" };
        var supervisor = Create(cli: cli, chainRaised: () => raised.Add(1));

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);
        Assert.Single(raised); // a full raise ends with the hook (the idle stopper re-arms through it)

        raised.Clear();
        var failing = Create(
            daemon: new FakeDaemon(_calls) { EnsureThrows = new IOException("UAC declined") },
            chainRaised: () => raised.Add(1));
        await Assert.ThrowsAsync<ChainException>(() =>
            failing.EnsureRunningAsync(null, CancellationToken.None));
        Assert.Empty(raised); // a failed raise has no "fully up" — no hook
    }

    [Fact]
    public async Task DaemonStepCarriesUacFlagOnlyWhenElevating()
    {
        var elevating = new StepCollector();
        var supervisor = Create(daemon: new FakeDaemon(_calls) { NeedsUac = true });
        await supervisor.EnsureRunningAsync(elevating, CancellationToken.None);

        var step = elevating.Steps.Single(s => s.Component == "Daemon" && s.Status == ChainStepStatus.Active);
        Assert.True(step.NeedsUac);
        Assert.Equal("starting (confirm UAC)", step.Detail);

        var direct = new StepCollector();
        var plain = Create(daemon: new FakeDaemon(_calls));
        await plain.EnsureRunningAsync(direct, CancellationToken.None);

        var plainStep = direct.Steps.Single(s => s.Component == "Daemon" && s.Status == ChainStepStatus.Active);
        Assert.False(plainStep.NeedsUac);
        Assert.Equal("starting", plainStep.Detail);
    }

    [Fact]
    public async Task DaemonFailureStopsEverything()
    {
        var daemon = new FakeDaemon(_calls)
        {
            EnsureThrows = new IOException("UAC declined"),
        };
        var supervisor = Create(daemon: daemon);

        await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.Equal(["daemon.ensure"], _calls);
    }

    [Fact]
    public async Task KeeperCrashFailsBeforeUp()
    {
        var keeper = new FakeKeeper(_calls) { EnsureThrows = new IOException("crashed immediately") };
        var supervisor = Create(keeper: keeper);

        await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.Equal(["daemon.ensure", "keeper.ensure"], _calls);
    }

    [Fact]
    public async Task UpNotLeadingToRunningFails()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin", BackendStateAfterUp = "NeedsLogin" };
        var supervisor = Create(cli: cli);

        await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.DoesNotContain("proxy.start", _calls);
    }

    [Fact]
    public async Task HealthIsPolledUntilAlive()
    {
        var attempts = 0;
        Task<bool> Health(CancellationToken _)
        {
            attempts++;
            return Task.FromResult(attempts >= 3);
        }

        var supervisor = Create(health: Health);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task HealthTimeoutFails()
    {
        var supervisor = Create(health: _ => Task.FromResult(false));

        await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));
    }

    /// <summary>
    /// A probe that hangs past the deadline is cut off by it: the phase must end at the deadline,
    /// not a full probe timeout later (ProxyHealthCheck folds cancellation into "not alive" the
    /// same way this fake does).
    /// </summary>
    [Fact]
    public async Task HealthDeadlineCapsAHangingProbe()
    {
        async Task<bool> Health(CancellationToken ct)
        {
            try
            {
                await Task.Delay(5000, ct); // longer than the whole phase budget
            }
            catch (OperationCanceledException)
            {
                // the probe reports "not alive" on cancellation instead of throwing
            }

            return false;
        }

        var supervisor = Create(health: Health, healthWaitSeconds: 1);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"the health phase took {stopwatch.Elapsed}, the deadline must cap it");
    }

    [Fact]
    public async Task StopRunsInReverseOrder()
    {
        var supervisor = Create();

        await supervisor.StopAsync(null, CancellationToken.None);

        Assert.Equal(
            ["proxy.stop", "cli.down", "daemon.kill", "keeper.kill"],
            _calls);
    }

    [Fact]
    public async Task StopRaisedLeavesTheLinksThatWereUpBefore()
    {
        var stoppedHooks = 0;
        // daemon, keeper and node were up before the launch; only the proxy was missing, and the
        // launch was cancelled while the proxy was starting
        var supervisor = Create(
            proxy: new FakeProxy(_calls) { StartThrows = new OperationCanceledException() },
            daemon: new FakeDaemon(_calls) { IsHttpListening = true, IsProcessAlive = true },
            keeper: new FakeKeeper(_calls) { IsRunning = true },
            cli: new FakeCli(_calls) { BackendState = "Running" },
            chainStopped: _ =>
            {
                stoppedHooks++;
                return Task.CompletedTask;
            });
        var raised = new ChainRaisedLinks();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            supervisor.EnsureRunningAsync(null, raised, CancellationToken.None));
        _calls.Clear();

        await supervisor.StopRaisedAsync(raised, null, CancellationToken.None);

        Assert.True(raised.Proxy);
        Assert.False(raised.Daemon || raised.Keeper || raised.Node);
        Assert.Equal(["proxy.stop"], _calls);
        Assert.Equal(0, stoppedHooks); // the chain is still partly up — its idle stopper stays
    }

    [Fact]
    public async Task StopRaisedOfAChainThatWasFullyDownIsTheFullStop()
    {
        var stoppedHooks = 0;
        var supervisor = Create(
            proxy: new FakeProxy(_calls) { StartThrows = new OperationCanceledException() },
            cli: new FakeCli(_calls) { BackendState = "Stopped" },
            chainStopped: _ =>
            {
                stoppedHooks++;
                return Task.CompletedTask;
            });
        var raised = new ChainRaisedLinks();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            supervisor.EnsureRunningAsync(null, raised, CancellationToken.None));
        _calls.Clear();

        await supervisor.StopRaisedAsync(raised, null, CancellationToken.None);

        Assert.Equal(["proxy.stop", "cli.down", "daemon.kill", "keeper.kill"], _calls);
        Assert.Equal(1, stoppedHooks); // nothing of ours is left — the idle stopper goes too
    }

    [Fact]
    public async Task StopRaisedOfAChainThatWasFullyUpDoesNothing()
    {
        var supervisor = Create(
            proxy: new FakeProxy(_calls) { IsRunning = true },
            daemon: new FakeDaemon(_calls) { IsHttpListening = true, IsProcessAlive = true },
            keeper: new FakeKeeper(_calls) { IsRunning = true },
            cli: new FakeCli(_calls) { BackendState = "Running" });
        var raised = new ChainRaisedLinks();
        await supervisor.EnsureRunningAsync(null, raised, CancellationToken.None);

        await supervisor.StopRaisedAsync(raised, null, CancellationToken.None);

        Assert.False(raised.Any);
        Assert.Empty(_calls);
    }

    /// <summary>A stale daemon (alive without its listener) is killed and started anew by the bring-up: it is the bring-up's to roll back.</summary>
    [Fact]
    public async Task AStaleDaemonRestartedByTheBringUpIsRecordedAsRaised()
    {
        var supervisor = Create(
            proxy: new FakeProxy(_calls) { StartThrows = new OperationCanceledException() },
            daemon: new FakeDaemon(_calls) { IsHttpListening = false, IsProcessAlive = true },
            keeper: new FakeKeeper(_calls) { IsRunning = true },
            cli: new FakeCli(_calls) { BackendState = "Running" });
        var raised = new ChainRaisedLinks();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            supervisor.EnsureRunningAsync(null, raised, CancellationToken.None));
        _calls.Clear();

        await supervisor.StopRaisedAsync(raised, null, CancellationToken.None);

        Assert.True(raised.Daemon);
        Assert.False(raised.Keeper);
        Assert.Equal(["proxy.stop", "daemon.kill"], _calls);
    }

    /// <summary>
    /// The record is made under the operation lock: a bring-up that waited for another one in
    /// flight finds the links up and records nothing — the other caller's links are not its own.
    /// </summary>
    [Fact]
    public async Task AConcurrentBringUpIsNotAttributedToTheWaitingOne()
    {
        var gate = new TaskCompletionSource();
        var daemon = new FakeDaemon(_calls) { EnsureGate = gate };
        var supervisor = Create(daemon: daemon, cli: new FakeCli(_calls) { BackendState = "Stopped" });
        var first = new ChainRaisedLinks();
        var second = new ChainRaisedLinks();

        var firstRun = supervisor.EnsureRunningAsync(null, first, CancellationToken.None);
        while (daemon.ConcurrentEnsures == 0)
        {
            await Task.Delay(10);
        }

        var secondRun = supervisor.EnsureRunningAsync(null, second, CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(firstRun, secondRun);

        Assert.True(first.Daemon && first.Keeper && first.Node && first.Proxy);
        Assert.False(second.Any);
    }

    /// <summary>
    /// A completed stop ends with the chain-stopped hook — the idle stopper is taken down through
    /// it, in both modes. The hook runs even when the caller's token is already cancelled (a
    /// cancelled chain stop must still leave no stoppers): the stop links receive
    /// <see cref="CancellationToken.None"/> — the fakes honor their token, so this passes only
    /// when the supervisor really detaches the stop from the caller's cancellation — and the hook
    /// receives no leftover token.
    /// </summary>
    [Fact]
    public async Task CompletedStopFiresTheChainStoppedHook()
    {
        var daemon = new FakeDaemon(_calls);
        var keeper = new FakeKeeper(_calls);
        var cli = new FakeCli(_calls);
        var tokens = new List<CancellationToken>();
        var supervisor = Create(daemon: daemon, keeper: keeper, cli: cli, chainStopped: ct =>
        {
            tokens.Add(ct);
            return Task.CompletedTask;
        });

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await supervisor.StopAsync(null, cancelled.Token);

        Assert.Equal(["proxy.stop", "cli.down", "daemon.kill", "keeper.kill"], _calls);
        Assert.Equal([CancellationToken.None], daemon.KillTokens);
        Assert.Equal([CancellationToken.None], keeper.KillTokens);
        Assert.Equal([CancellationToken.None], cli.DownTokens);
        Assert.Equal([CancellationToken.None], tokens);

        _calls.Clear();
        tokens.Clear();
        var socks5 = Create(mode: "socks5", chainStopped: ct =>
        {
            tokens.Add(ct);
            return Task.CompletedTask;
        });
        await socks5.StopAsync(null, CancellationToken.None);

        Assert.Equal(["proxy.stop"], _calls);
        Assert.Equal([CancellationToken.None], tokens);
    }

    /// <summary>A failed stop is not a completed stop: the daemon is still alive, the hook waits.</summary>
    [Fact]
    public async Task FailedStopDoesNotFireTheChainStoppedHook()
    {
        var stopped = false;
        var daemon = new FakeDaemon(_calls) { KillThrows = new IOException("access denied") };
        var supervisor = Create(daemon: daemon, chainStopped: _ =>
        {
            stopped = true;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.StopAsync(null, CancellationToken.None));

        Assert.False(stopped);
    }

    /// <summary>A daemon that refuses to die must not leave the keeper behind: every stop step still runs.</summary>
    [Fact]
    public async Task FailedDaemonKillStillStopsTheKeeper()
    {
        var daemon = new FakeDaemon(_calls) { KillThrows = new IOException("access denied") };
        var supervisor = Create(daemon: daemon);

        var error = await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.StopAsync(null, CancellationToken.None));

        Assert.Contains("daemon", error.Message);
        Assert.Equal(["proxy.stop", "cli.down", "daemon.kill", "keeper.kill"], _calls);
    }

    /// <summary>A surviving keeper is a stop failure (Q-04): the stop reports it and the hook waits.</summary>
    [Fact]
    public async Task ASurvivingKeeperFailsTheStopAndHoldsTheHook()
    {
        var keeper = new FakeKeeper(_calls) { RefusesToDie = true };
        var hookRan = false;
        var supervisor = Create(keeper: keeper, chainStopped: _ =>
        {
            hookRan = true;
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.StopAsync(null, CancellationToken.None));

        Assert.Contains("keeper", error.Message);
        Assert.False(hookRan);
    }

    /// <summary>
    /// A foreign listener holding the daemon port is not a surviving daemon: the pipe decides —
    /// the stop succeeds and the chain-stopped hook (the idle stopper's end) still fires.
    /// </summary>
    [Fact]
    public async Task AForeignListenerOnTheDaemonPortDoesNotFailTheStop()
    {
        var daemon = new FakeDaemon(_calls) { PortStaysForeignAfterKill = true };
        var keeper = new FakeKeeper(_calls);
        var hookRan = false;
        var supervisor = Create(daemon: daemon, keeper: keeper, chainStopped: _ =>
        {
            hookRan = true;
            return Task.CompletedTask;
        });

        await supervisor.StopAsync(null, CancellationToken.None);

        Assert.Equal(["proxy.stop", "cli.down", "daemon.kill", "keeper.kill"], _calls);
        Assert.True(hookRan);
    }

    /// <summary>socks5 egress: instead of the three tailscale links, one check of the provided upstream.</summary>
    [Fact]
    public async Task Socks5ColdStartChecksEgressThenStartsProxy()
    {
        var supervisor = Create(mode: "socks5", upstream: _ => Task.FromResult(true), healthWaitSeconds: 1);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Equal(["proxy.start"], _calls); // no daemon/keeper/cli touches at all
    }

    [Fact]
    public async Task Socks5EgressStepRunsBeforeProxyAndHealth()
    {
        var steps = new StepCollector();
        var supervisor = Create(mode: "socks5", upstream: _ => Task.FromResult(true), healthWaitSeconds: 1);

        await supervisor.EnsureRunningAsync(steps, CancellationToken.None);

        Assert.Equal("Egress", steps.Steps[0].Component);
        Assert.Equal(ChainStepStatus.Active, steps.Steps[0].Status);
        Assert.Equal("Egress", steps.Steps.First(s => s.Status == ChainStepStatus.Done).Component);
        Assert.Contains(steps.Steps, s => s.Component == "Proxy" && s.Status == ChainStepStatus.Done);
        Assert.Contains(steps.Steps, s => s.Component == "Health" && s.Status == ChainStepStatus.Done);
        Assert.DoesNotContain(steps.Steps, s => s.Component is "Daemon" or "Keeper" or "Node");
    }

    [Fact]
    public async Task Socks5EgressFailureKeepsTheProxyDown()
    {
        var supervisor = Create(mode: "socks5", upstream: _ => Task.FromResult(false), healthWaitSeconds: 1);

        var ex = await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.Contains("egress", ex.Message);
        Assert.DoesNotContain("proxy.start", _calls);
    }

    [Fact]
    public async Task Socks5StopOnlyStopsTheProxy()
    {
        var supervisor = Create(mode: "socks5");

        await supervisor.StopAsync(null, CancellationToken.None);

        Assert.Equal(["proxy.stop"], _calls);
    }

    /// <summary>
    /// The GUI stop during a bring-up cancels it: the cancellation must reach the caller as
    /// cancellation, not as a link failure (the UI then brings down what got raised). A link
    /// phase must not fold it into ChainException.
    /// </summary>
    [Fact]
    public async Task CancellationSurfacesUnwrappedFromEveryPhase()
    {
        var daemon = new FakeDaemon(_calls) { EnsureThrows = new OperationCanceledException() };
        var daemonSupervisor = Create(daemon: daemon);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            daemonSupervisor.EnsureRunningAsync(null, CancellationToken.None));
        Assert.DoesNotContain("proxy.start", _calls);

        _calls.Clear();
        var proxy = new FakeProxy(_calls) { StartThrows = new OperationCanceledException() };
        var proxySupervisor = Create(proxy: proxy);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            proxySupervisor.EnsureRunningAsync(null, CancellationToken.None));
        // the cancellation hit at the proxy phase — the last one of the tailscale order
        Assert.Equal(["daemon.ensure", "keeper.ensure", "cli.up", "proxy.start"], _calls);
    }

    /// <summary>socks5: the stop lands while the egress check keeps polling to its deadline.</summary>
    [Fact]
    public async Task Socks5EgressCheckIsCancellable()
    {
        using var cts = new CancellationTokenSource();
        async Task<bool> Upstream(CancellationToken ct)
        {
            cts.Cancel(); // the stop arrives while the check runs
            await Task.Delay(5000, ct); // the next poll attempt is cut by the cancellation
            return false;
        }

        var supervisor = Create(mode: "socks5", upstream: Upstream, healthWaitSeconds: 30);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            supervisor.EnsureRunningAsync(null, cts.Token));

        Assert.DoesNotContain("proxy.start", _calls); // the proxy never came up past a cancelled egress
    }

    /// <summary>
    /// A hanging upstream must end as a <see cref="ChainException"/> at the health deadline, never
    /// as a cancellation: the egress check's own deadlines (its SOCKS timeout, the attempt
    /// deadline) fire while the caller token is live, and the caller would see a false
    /// "Cancelled" instead of "the upstream does not pass the check".
    /// </summary>
    [Fact]
    public async Task HangingUpstreamEgressEndsAtTheDeadlineNotAsACancellation()
    {
        using var caller = new CancellationTokenSource();
        async Task<bool> Upstream(CancellationToken attempt)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, attempt);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                return false; // the check's internal deadline, folded into "not reachable yet"
            }

            return false;
        }

        var supervisor = Create(mode: "socks5", upstream: Upstream, healthWaitSeconds: 2);

        var error = await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, caller.Token));

        Assert.Contains("egress", error.Message);
        Assert.DoesNotContain("proxy.start", _calls); // nothing came up past a dead egress
    }

    [Fact]
    public async Task Socks5StateOnlyCoversProxyAndEgress()
    {
        var proxy = new FakeProxy(_calls) { IsRunning = true };
        var supervisor = Create(
            proxy: proxy,
            mode: "socks5",
            upstream: _ => Task.FromResult(true),
            health: _ => Task.FromResult(true));

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.True(state.UpstreamReachable);
        Assert.True(state.ProxyRunning == true);
        Assert.True(state.ProxyHealthy);
        Assert.True(state.IsFullyUp);
        Assert.Empty(_calls); // the tailscale links were not consulted for the state either
    }

    /// <summary>
    /// The skipped socks5 shape (forwardProxy.enabled=false): the bring-up is the egress check
    /// and health against the external endpoint — the proxy link does not exist at all.
    /// </summary>
    [Fact]
    public async Task Socks5SkippedBringUpRunsEgressThenHealthWithoutTheProxy()
    {
        var supervisor = Create(
            mode: "socks5",
            upstream: _ => Task.FromResult(true),
            health: _ => Task.FromResult(true),
            proxyDisabled: true);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Empty(_calls); // no proxy start, no daemon/keeper/cli touches
    }

    /// <summary>
    /// The tailscale mode with the local proxy off is a config error: the proxy is load-bearing
    /// there (DoH resolution, dialing by IP) — the bring-up refuses before touching anything.
    /// </summary>
    [Fact]
    public async Task TailscaleModeWithDisabledProxyRefusesBringUp()
    {
        var supervisor = Create(proxyDisabled: true);

        var ex = await Assert.ThrowsAsync<ChainException>(() => supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.Contains("forwardProxy.enabled=false", ex.Message);
        Assert.Contains("required in the tailscale mode", ex.Message);
        Assert.Empty(_calls); // nothing was started before the refusal
    }

    [Fact]
    public async Task Socks5SkippedStateReportsNotApplicableProxy()
    {
        var supervisor = Create(
            mode: "socks5",
            upstream: _ => Task.FromResult(true),
            health: _ => Task.FromResult(true),
            proxyDisabled: true);

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.Null(state.ProxyRunning); // "not applicable", not false — there is no local proxy here
        Assert.True(state.ProxyHealthy);
        Assert.True(state.UpstreamReachable);
        Assert.True(state.IsFullyUp);
        Assert.False(state.IsStopped);
    }

    [Fact]
    public async Task Socks5SkippedHealthFailsWithoutTheEgress()
    {
        var supervisor = Create(
            mode: "socks5",
            upstream: _ => Task.FromResult(false),
            health: _ => Task.FromResult(true),
            proxyDisabled: true);

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.Null(state.ProxyRunning);
        Assert.False(state.ProxyHealthy); // health never runs past a dead egress
        Assert.False(state.UpstreamReachable);
        Assert.False(state.IsFullyUp);
        Assert.True(state.IsStopped); // nothing of ours is up — the normal resting state
    }

    /// <summary>The skipped shape stop: nothing of ours is running — no steps, and the idle stopper still goes down.</summary>
    [Fact]
    public async Task Socks5SkippedStopTouchesNothingAndFiresTheHook()
    {
        var tokens = new List<CancellationToken>();
        var supervisor = Create(
            mode: "socks5",
            proxyDisabled: true,
            chainStopped: ct =>
            {
                tokens.Add(ct);
                return Task.CompletedTask;
            });

        await supervisor.StopAsync(null, CancellationToken.None);

        Assert.Empty(_calls);
        Assert.Equal([CancellationToken.None], tokens);
    }

    /// <summary>The skipped shape draw rail has no Proxy component — a failing egress reports Egress, nothing else.</summary>
    [Fact]
    public async Task Socks5SkippedEgressFailureKeepsEverythingDown()
    {
        var supervisor = Create(
            mode: "socks5",
            upstream: _ => Task.FromResult(false),
            healthWaitSeconds: 1,
            proxyDisabled: true);

        var ex = await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.Contains("egress", ex.Message);
        Assert.Empty(_calls);
    }

    // ----- http mode: the provided HTTP upstream, the local proxy never runs -----

    /// <summary>
    /// The http mode is a skipped shape by definition — even with forwardProxy.enabled=true:
    /// the bring-up is the egress check and health against the provided HTTP upstream, and no
    /// proxy link exists (the HTTP upstream speaks CONNECT itself).
    /// </summary>
    [Fact]
    public async Task HttpBringUpRunsEgressThenHealthWithoutTheProxy()
    {
        var supervisor = Create(
            mode: "http",
            upstream: _ => Task.FromResult(true),
            health: _ => Task.FromResult(true),
            proxyDisabled: false);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Empty(_calls); // no proxy start (the flag is ignored), no daemon/keeper/cli touches
    }

    [Fact]
    public async Task HttpEgressFailureKeepsEverythingDownAndNamesTheUpstreamKind()
    {
        var supervisor = Create(
            mode: "http",
            upstream: _ => Task.FromResult(false),
            healthWaitSeconds: 1);

        var ex = await Assert.ThrowsAsync<ChainException>(() =>
            supervisor.EnsureRunningAsync(null, CancellationToken.None));

        Assert.Contains("HTTP upstream", ex.Message);
        Assert.DoesNotContain("proxy.start", _calls);
    }

    [Fact]
    public async Task HttpStopTouchesNothingAndFiresTheHook()
    {
        var tokens = new List<CancellationToken>();
        var supervisor = Create(mode: "http", chainStopped: ct =>
        {
            tokens.Add(ct);
            return Task.CompletedTask;
        });

        await supervisor.StopAsync(null, CancellationToken.None);

        Assert.Empty(_calls); // the external upstream is not ours to stop
        Assert.Equal([CancellationToken.None], tokens);
    }

    [Fact]
    public async Task HttpStateReportsNotApplicableProxy()
    {
        var supervisor = Create(
            mode: "http",
            upstream: _ => Task.FromResult(true),
            health: _ => Task.FromResult(true),
            proxyDisabled: false);

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.Null(state.ProxyRunning); // "not applicable" — there is no local proxy in this mode
        Assert.True(state.ProxyHealthy);
        Assert.True(state.UpstreamReachable);
        Assert.True(state.IsFullyUp);
        Assert.False(state.IsStopped);
    }

    [Fact]
    public async Task HttpStateWithDeadEgressIsAStoppedChain()
    {
        var supervisor = Create(
            mode: "http",
            upstream: _ => Task.FromResult(false),
            health: _ => Task.FromResult(true));

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.Null(state.ProxyRunning);
        Assert.False(state.ProxyHealthy); // health never runs past a dead egress
        Assert.False(state.UpstreamReachable);
        Assert.True(state.IsStopped);
    }

    [Fact]
    public async Task TailscaleStateLeavesUpstreamUnknown()
    {
        var daemon = new FakeDaemon(_calls) { IsHttpListening = true, IsProcessAlive = true };
        var keeper = new FakeKeeper(_calls) { IsRunning = true };
        var cli = new FakeCli(_calls) { BackendState = "Running" };
        var proxy = new FakeProxy(_calls) { IsRunning = true };
        var supervisor = Create(proxy, daemon, keeper, cli, health: _ => Task.FromResult(true));

        var state = await supervisor.GetStateAsync(CancellationToken.None);

        Assert.Null(state.UpstreamReachable);
        Assert.True(state.IsFullyUp);
    }

    private sealed class FakeProxy(List<string> calls) : IProxyControl
    {
        public bool IsRunning { get; set; }

        public Exception? StartThrows { get; init; }

        public Task StartAsync(CancellationToken ct)
        {
            calls.Add("proxy.start");
            if (StartThrows is not null)
            {
                throw StartThrows;
            }

            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            calls.Add("proxy.stop");
            return Task.CompletedTask;
        }
    }

    /// <summary>Synchronous step collector (Progress&lt;T&gt; posts asynchronously — unusable in asserts).</summary>
    private sealed class StepCollector : IProgress<ChainStep>
    {
        public List<ChainStep> Steps { get; } = [];

        public void Report(ChainStep step) => Steps.Add(step);
    }

    private sealed class FakeDaemon(List<string> calls) : ISidecarDaemon
    {
        public bool IsHttpListening { get; set; }

        public bool IsProcessAlive { get; set; }

        public Exception? EnsureThrows { get; init; }

        /// <summary>When set, EnsureStartedAsync parks until it completes (a UAC prompt that is still open).</summary>
        public TaskCompletionSource? EnsureGate { get; init; }

        public int ConcurrentEnsures { get; private set; }

        public int MaxConcurrentEnsures { get; private set; }

        public Exception? KillThrows { get; init; }

        /// <summary>A foreign process keeps holding the daemon port after the kill (the pipe is closed).</summary>
        public bool PortStaysForeignAfterKill { get; init; }

        /// <summary>The tokens the stop link received: production must hand it CancellationToken.None.</summary>
        public List<CancellationToken> KillTokens { get; } = [];

        public bool NeedsUac { get; init; }

        public bool StartNeedsUac() => NeedsUac;

        public async Task EnsureStartedAsync(IProgress<string>? progress, CancellationToken ct)
        {
            calls.Add("daemon.ensure");
            MaxConcurrentEnsures = Math.Max(MaxConcurrentEnsures, ++ConcurrentEnsures);
            try
            {
                if (EnsureGate is not null)
                {
                    await EnsureGate.Task.WaitAsync(ct);
                }

                if (EnsureThrows is not null)
                {
                    throw EnsureThrows;
                }

                IsHttpListening = true;
                IsProcessAlive = true;
            }
            finally
            {
                ConcurrentEnsures--;
            }
        }

        public Task KillAsync(CancellationToken ct)
        {
            KillTokens.Add(ct);
            ct.ThrowIfCancellationRequested(); // the fake honours the token exactly like production would
            calls.Add("daemon.kill");
            if (KillThrows is not null)
            {
                throw KillThrows;
            }

            IsProcessAlive = false;
            if (!PortStaysForeignAfterKill)
            {
                IsHttpListening = false;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeKeeper(List<string> calls) : ISidecarKeeper
    {
        public bool IsRunning { get; set; }

        public IOException? EnsureThrows { get; init; }

        /// <summary>The kill cannot take the keeper down (production KillAsync reports that as a failure).</summary>
        public bool RefusesToDie { get; init; }

        public List<CancellationToken> KillTokens { get; } = [];

        public Task EnsureStartedAsync(CancellationToken ct)
        {
            calls.Add("keeper.ensure");
            if (EnsureThrows is not null)
            {
                throw EnsureThrows;
            }

            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken ct)
        {
            KillTokens.Add(ct);
            ct.ThrowIfCancellationRequested();
            calls.Add("keeper.kill");
            if (RefusesToDie)
            {
                throw new IOException("keeper: watch-ipn is still running after the kill");
            }

            IsRunning = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCli(List<string> calls) : ISidecarCli
    {
        public string? BackendState { get; set; }

        public string? BackendStateAfterUp { get; set; }

        /// <summary>The authentication URL the login reports (null — none).</summary>
        public string? AuthUrl { get; init; }

        /// <summary>The login-server argument of the last login; null — the default control server.</summary>
        public string? LastLoginServer { get; private set; }

        public IOException? LoginThrows { get; init; }

        /// <summary>The tokens the stop link received: production must hand it CancellationToken.None.</summary>
        public List<CancellationToken> DownTokens { get; } = [];

        public Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
            Task.FromResult(new CliResult(0, string.Join(' ', arguments)));

        public Task<string?> GetBackendStateAsync(CancellationToken ct) =>
            Task.FromResult(BackendState);

        /// <summary>The node's own ControlURL (its prefs); a login binds the node to its server.</summary>
        public string? ControlUrl { get; set; }

        /// <summary>The login-server argument of the last up; null — the default control server.</summary>
        public string? LastUpServer { get; private set; }

        public Task<string?> GetControlUrlAsync(CancellationToken ct) => Task.FromResult(ControlUrl);

        public Task<CliResult> UpAsync(string? loginServer, CancellationToken ct)
        {
            calls.Add("cli.up");
            LastUpServer = loginServer;
            BackendState = BackendStateAfterUp ?? "Running";
            return Task.FromResult(new CliResult(0, "up"));
        }

        public Task<CliResult> DownAsync(CancellationToken ct)
        {
            DownTokens.Add(ct);
            ct.ThrowIfCancellationRequested();
            calls.Add("cli.down");
            BackendState = "Stopped";
            return Task.FromResult(new CliResult(0, "down"));
        }

        public Task<CliResult> LoginAsync(string? loginServer, Action<string>? onAuthUrl, CancellationToken ct)
        {
            calls.Add(loginServer is null ? "cli.login" : $"cli.login:{loginServer}");
            LastLoginServer = loginServer;
            if (LoginThrows is not null)
            {
                throw LoginThrows;
            }

            ControlUrl = loginServer;

            if (AuthUrl is not null)
            {
                onAuthUrl?.Invoke(AuthUrl);
            }

            return Task.FromResult(new CliResult(0, "login", AuthUrl));
        }
    }

    /// <summary>
    /// The result file of an elevated command lives in the pinned elevated directory, and the
    /// helper (an administrator) writes nowhere else — the asking side must have the directory
    /// in place even when the daemon never ran, so the runner creates it itself.
    /// </summary>
    [Fact]
    public async Task ElevatedRunnerCreatesThePinnedDirectoryForTheResultFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pnr-elevated-{Guid.NewGuid():N}");
        ElevatedHelperTrust.PinnedRootOverride = root;
        try
        {
            var pinned = Path.Combine(root, "PrivateNetRouting", "elevated");
            var logPath = Path.Combine(Path.GetTempPath(), $"pnr-elevated-{Guid.NewGuid():N}.log");
            var log = new FileLog(logPath);
            string? writtenTo = null;
            var runner = new ElevatedRunner(new ChainTailscaleConfig(), log, elevatedBypass: true, executor: (_, resultFile) =>
            {
                writtenTo = resultFile;
                File.WriteAllText(resultFile, "ok test");
                return 0;
            });

            var result = await runner.RunAsync("--elevated-test-command", CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Equal("test", result.Detail); // the helper's own detail, not a canned one
            Assert.NotNull(writtenTo);
            Assert.True(Directory.Exists(pinned));
            Assert.True(File.Exists(Path.Combine(pinned, Path.GetFileName(writtenTo!))));
            Assert.False(File.Exists(writtenTo)); // the collected result is removed
            log.Dispose();
            File.Delete(logPath);
        }
        finally
        {
            ElevatedHelperTrust.PinnedRootOverride = null;
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Two bring-ups at once (the card and an update window) must not interleave: the second waits and then finds the chain up.</summary>
    [Fact]
    public async Task ConcurrentBringUpsAreSerialised()
    {
        var gate = new TaskCompletionSource();
        var daemon = new FakeDaemon(_calls) { EnsureGate = gate };
        var supervisor = Create(daemon: daemon);

        var first = supervisor.EnsureRunningAsync(null, CancellationToken.None);
        while (daemon.ConcurrentEnsures == 0)
        {
            await Task.Delay(10);
        }

        var second = supervisor.EnsureRunningAsync(null, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(second.IsCompleted); // waits for the first operation instead of racing it
        Assert.Single(_calls, c => c == "daemon.ensure");

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, daemon.MaxConcurrentEnsures);
        Assert.Single(_calls, c => c == "daemon.ensure"); // the second pass saw the daemon alive
    }

    [Fact]
    public async Task AStopWaitsForABringUpInFlight()
    {
        var gate = new TaskCompletionSource();
        var daemon = new FakeDaemon(_calls) { EnsureGate = gate };
        var supervisor = Create(daemon: daemon);
        var bringUp = supervisor.EnsureRunningAsync(null, CancellationToken.None);
        while (daemon.ConcurrentEnsures == 0)
        {
            await Task.Delay(10);
        }

        var stop = supervisor.StopAsync(null, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(stop.IsCompleted);
        Assert.DoesNotContain("daemon.kill", _calls); // it would have found nothing yet to kill

        gate.SetResult();
        await Task.WhenAll(bringUp, stop);

        Assert.True(_calls.IndexOf("daemon.ensure") < _calls.IndexOf("daemon.kill"));
    }

    [Fact]
    public async Task ACancelledWaiterLeavesTheOperationLockFree()
    {
        var gate = new TaskCompletionSource();
        var daemon = new FakeDaemon(_calls) { EnsureGate = gate };
        var supervisor = Create(daemon: daemon);
        var first = supervisor.EnsureRunningAsync(null, CancellationToken.None);
        while (daemon.ConcurrentEnsures == 0)
        {
            await Task.Delay(10);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.EnsureRunningAsync(null, cts.Token));

        gate.SetResult();
        await first;
        await supervisor.StopAsync(null, CancellationToken.None); // the lock was not leaked
    }

    /// <summary>
    /// A cancelled bring-up must surface as OperationCanceledException from the elevated runner
    /// (never as a failed result that the chain wraps into a daemon failure), and it must not raise
    /// the UAC prompt for an operation that is already cancelled.
    /// </summary>
    [Fact]
    public async Task ElevatedRunnerCancellationSurfacesAsOperationCanceled()
    {
        if (WindowsPrivilege.IsElevated())
        {
            return; // an elevated process takes the in-process path, which has no wait to cancel
        }

        var root = Path.Combine(Path.GetTempPath(), $"pnr-elevated-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"pnr-elevated-{Guid.NewGuid():N}.log");
        using var log = new FileLog(logPath);
        var runner = new ElevatedRunner(new ChainTailscaleConfig(), log);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("--elevated-test-command", cts.Token));

        // nothing was prepared for a helper: not even the pinned directory was created
        Assert.False(Directory.Exists(Path.Combine(root, "PrivateNetRouting", "elevated")));
    }

    public void Dispose()
    {
        foreach (var log in _logs)
        {
            log.Dispose();
        }

        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }
}
