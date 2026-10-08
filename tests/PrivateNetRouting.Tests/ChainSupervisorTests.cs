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
        Action? chainRaised = null)
    {
        var log = new FileLog(_logPath);
        _logs.Add(log);
        return new ChainSupervisor(
            new ChainConfig { HealthWaitSeconds = 5 },
            proxy ?? new FakeProxy(_calls),
            daemon ?? new FakeDaemon(_calls),
            keeper ?? new FakeKeeper(_calls),
            cli ?? new FakeCli(_calls),
            log,
            health ?? (_ => Task.FromResult(true)),
            chainRaised);
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

    [Fact]
    public async Task StopRunsInReverseOrder()
    {
        var supervisor = Create();

        await supervisor.StopAsync(null, CancellationToken.None);

        Assert.Equal(
            ["proxy.stop", "cli.down", "daemon.kill", "keeper.kill"],
            _calls);
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
