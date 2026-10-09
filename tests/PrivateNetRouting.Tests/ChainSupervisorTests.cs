using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Tests;

/// <summary>Chain orchestration on fakes: order, idempotence, link failures.</summary>
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
        Func<CancellationToken, Task<bool>>? upstream = null)
    {
        var log = new FileLog(_logPath);
        _logs.Add(log);
        return new ChainSupervisor(
            new ChainConfig { HealthWaitSeconds = healthWaitSeconds, Mode = mode ?? "tailscale" },
            proxy ?? new FakeProxy(_calls),
            daemon ?? new FakeDaemon(_calls),
            keeper ?? new FakeKeeper(_calls),
            cli ?? new FakeCli(_calls),
            log,
            health ?? (_ => Task.FromResult(true)),
            chainRaised,
            upstream);
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

    [Fact]
    public async Task ColdStartRunsInOrder()
    {
        var cli = new FakeCli(_calls) { BackendState = "NeedsLogin" };
        var supervisor = Create(cli: cli);

        await supervisor.EnsureRunningAsync(null, CancellationToken.None);

        Assert.Equal(
            ["daemon.ensure", "keeper.ensure", "cli.up", "proxy.start"],
            _calls);
        Assert.Equal("Running", cli.BackendState); // after up the node must become Running
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
        Assert.True(state.ProxyRunning);
        Assert.True(state.ProxyHealthy);
        Assert.True(state.IsFullyUp);
        Assert.Empty(_calls); // the tailscale links were not consulted for the state either
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

        public Task StartAsync(CancellationToken ct)
        {
            IsRunning = true;
            calls.Add("proxy.start");
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

        public IOException? EnsureThrows { get; init; }

        public bool NeedsUac { get; init; }

        public bool StartNeedsUac() => NeedsUac;

        public Task EnsureStartedAsync(IProgress<string>? progress, CancellationToken ct)
        {
            calls.Add("daemon.ensure");
            if (EnsureThrows is not null)
            {
                throw EnsureThrows;
            }

            IsHttpListening = true;
            IsProcessAlive = true;
            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken ct)
        {
            IsHttpListening = false;
            IsProcessAlive = false;
            calls.Add("daemon.kill");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeKeeper(List<string> calls) : ISidecarKeeper
    {
        public bool IsRunning { get; set; }

        public IOException? EnsureThrows { get; init; }

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
            IsRunning = false;
            calls.Add("keeper.kill");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCli(List<string> calls) : ISidecarCli
    {
        public string? BackendState { get; set; }

        public string? BackendStateAfterUp { get; set; }

        public Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
            Task.FromResult(new CliResult(0, string.Join(' ', arguments)));

        public Task<string?> GetBackendStateAsync(CancellationToken ct) =>
            Task.FromResult(BackendState);

        public Task<CliResult> UpAsync(CancellationToken ct)
        {
            calls.Add("cli.up");
            BackendState = BackendStateAfterUp ?? "Running";
            return Task.FromResult(new CliResult(0, "up"));
        }

        public Task<CliResult> DownAsync(CancellationToken ct)
        {
            calls.Add("cli.down");
            BackendState = "Stopped";
            return Task.FromResult(new CliResult(0, "down"));
        }
    }

    /// <summary>
    /// The result file of an elevated command lives in the statedir, but the statedir is normally
    /// created by the daemon — an elevated guard command on a machine where the daemon never ran
    /// must still be able to report, so the runner creates the directory itself.
    /// </summary>
    [Fact]
    public async Task ElevatedRunnerCreatesStateDirForTheResultFile()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"pnr-elevated-{Guid.NewGuid():N}", "nested");
        var config = new ChainConfig { StateDir = stateDir };
        var logPath = Path.Combine(Path.GetTempPath(), $"pnr-elevated-{Guid.NewGuid():N}.log");
        var log = new FileLog(logPath);
        string? writtenTo = null;
        var runner = new ElevatedRunner(config, log, elevatedBypass: true, executor: (_, resultFile) =>
        {
            writtenTo = resultFile;
            File.WriteAllText(resultFile, "ok test");
            return 0;
        });

        var result = await runner.RunAsync("--elevated-test-command", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(writtenTo);
        Assert.True(Directory.Exists(stateDir));
        Assert.Equal("ok test", File.ReadAllText(writtenTo).Trim());
        Directory.Delete(stateDir, recursive: true);
        log.Dispose();
        File.Delete(logPath);
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
