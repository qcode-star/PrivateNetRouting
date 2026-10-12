using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>Claude launcher: bypass list, the "check → up → launch" order, modes, warnings.</summary>
public class LauncherTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-launcher-{Guid.NewGuid():N}.log");
    private readonly FileLog _log;
    private readonly FakeChain _chain = new();
    private readonly FakeProcessFinder _processes = new();
    private readonly List<ProcessStartInfo> _started = [];

    public LauncherTests()
    {
        _log = new FileLog(_logPath);
    }

    private ClaudeLauncher Create(
        AppConfig? config = null,
        bool healthy = true,
        string? policy = "http://127.0.0.1:18082",
        ChainException? chainFailure = null,
        IRunningAppCloser? closer = null)
    {
        config ??= new AppConfig
        {
            ForwardProxy = new ForwardProxyConfig { Listen = "127.0.0.1:18082" },
            Launcher = new LauncherConfig
            {
                AliasPath = @"C:\fake\claude.exe",
                BypassDomains = ["corp.example.com", ".team.example.net"],
            },
        };
        _chain.Failure = chainFailure;
        return new ClaudeLauncher(
            config,
            _chain,
            _ => Task.FromResult(healthy),
            _processes,
            _log,
            () => policy,
            psi =>
            {
                _started.Add(psi);
                return null;
            },
            closer);
    }

    [Fact]
    public void BypassListHasBothFormsOfEveryDomain()
    {
        var list = ClaudeBypassList.Build(["corp.example.com", ".team.example.net"]);

        Assert.Equal(
            "<local>;localhost;127.0.0.1;[::1];10.0.0.0/8;172.16.0.0/12;192.168.0.0/16;169.254.0.0/16;100.64.0.0/10;*.local"
            + ";corp.example.com;*.corp.example.com;team.example.net;*.team.example.net",
            list);
    }

    [Fact]
    public void BypassListWithoutDomainsIsBaseOnly()
    {
        var list = ClaudeBypassList.Build([]);

        Assert.StartsWith("<local>;localhost;127.0.0.1", list);
        Assert.EndsWith("100.64.0.0/10;*.local", list);
    }

    [Fact]
    public async Task HealthyChainLaunchesWithoutRaising()
    {
        var launcher = Create(healthy: true);

        var result = await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(LaunchOutcome.Launched, result.Outcome);
        Assert.False(result.ChainRaised);
        Assert.Equal(0, _chain.EnsureCalls);
        var psi = Assert.Single(_started);
        Assert.Equal(@"C:\fake\claude.exe", psi.FileName);
        Assert.Equal(2, psi.ArgumentList.Count);
        Assert.Equal("--proxy-server=http://127.0.0.1:18082", psi.ArgumentList[0]);
        Assert.Equal(
            "--proxy-bypass-list=<local>;localhost;127.0.0.1;[::1];10.0.0.0/8;172.16.0.0/12;192.168.0.0/16;169.254.0.0/16;100.64.0.0/10;*.local;corp.example.com;*.corp.example.com;team.example.net;*.team.example.net",
            psi.ArgumentList[1]);
    }

    [Fact]
    public async Task DeadChainIsRaisedBeforeLaunch()
    {
        var launcher = Create(healthy: false);

        var result = await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(LaunchOutcome.Launched, result.Outcome);
        Assert.True(result.ChainRaised);
        Assert.Equal(1, _chain.EnsureCalls);
        Assert.Single(_started);
    }

    /// <summary>The skipped socks5 shape: the flags point Claude at the external mixed inbound.</summary>
    [Fact]
    public async Task SkippedShapeLaunchCarriesTheExternalEndpoint()
    {
        var config = new AppConfig
        {
            Chain = new ChainConfig { Mode = "socks5" },
            ForwardProxy = new ForwardProxyConfig
            {
                Listen = "127.0.0.1:18082",
                SkipInSocks5Mode = true,
                ExternalAppsEndpoint = "192.0.2.10:7080",
            },
            Launcher = new LauncherConfig
            {
                AliasPath = @"C:\fake\claude.exe",
                BypassDomains = ["corp.example.com"],
            },
        };
        var launcher = Create(config, policy: "http://192.0.2.10:7080");

        var result = await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(LaunchOutcome.Launched, result.Outcome);
        Assert.Equal("http://192.0.2.10:7080", launcher.ProxyUrl);
        var psi = Assert.Single(_started);
        Assert.Equal("--proxy-server=http://192.0.2.10:7080", psi.ArgumentList[0]);
        Assert.DoesNotContain("missing or different", ReadLog()); // the policy matching the endpoint is recognized
    }

    [Fact]
    public async Task CheckOnlyRaisesChainButDoesNotStartClaude()
    {
        var launcher = Create(healthy: false);

        var result = await launcher.LaunchAsync(new LauncherOptions(CheckOnly: true), null, CancellationToken.None);

        Assert.Equal(LaunchOutcome.CheckOnly, result.Outcome);
        Assert.Equal(1, _chain.EnsureCalls);
        Assert.Empty(_started);
        Assert.DoesNotContain("WARN", ReadLog()); // policy/processes are not checked in check-only
    }

    [Fact]
    public async Task ChainFailureStopsLaunch()
    {
        var launcher = Create(healthy: false, chainFailure: new ChainException("daemon", new IOException("UAC declined")));

        await Assert.ThrowsAsync<ChainException>(() =>
            launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None));

        Assert.Empty(_started);
    }

    [Fact]
    public async Task PolicyMismatchWarnsButLaunches()
    {
        var launcher = Create(policy: "http://127.0.0.1:9999");

        await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.Single(_started);
        Assert.Contains("is missing or different", ReadLog());
    }

    [Fact]
    public async Task ClaudeWithoutProxyFlagWarnsButLaunches()
    {
        _processes.Processes = [new FoundProcess(42, "claude-desktop.exe")];
        var launcher = Create();

        await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.Single(_started);
        Assert.Contains("without --proxy-server", ReadLog());
    }

    [Fact]
    public async Task RestartClosesTheRunningClaudeBeforeStarting()
    {
        _processes.Processes = [new FoundProcess(42, "claude-desktop.exe")];
        var closer = new FakeAppCloser { Alive = { 42 } };
        var launcher = Create(closer: closer);

        var result = await launcher.LaunchAsync(new LauncherOptions(CheckOnly: false, RestartRunning: true), null, CancellationToken.None);

        Assert.True(result.RestartedExisting);
        Assert.Single(_started); // the launch itself always happens
        Assert.Equal(new[] { 42 }, closer.GracefulCalls);
        Assert.Empty(closer.Alive);
        Assert.Contains("closed to restart through the proxy", ReadLog());
    }

    [Fact]
    public async Task RunningClaudeWithOurProxyIsOnlyReported()
    {
        _processes.Processes = [new FoundProcess(42, "claude-desktop.exe --proxy-server=http://127.0.0.1:18082")];
        var closer = new FakeAppCloser { Alive = { 42 } };
        var launcher = Create(closer: closer);

        var running = launcher.InspectRunning();
        await launcher.LaunchAsync(new LauncherOptions(CheckOnly: false, RestartRunning: true), null, CancellationToken.None);

        Assert.Equal(RunningAppState.Proxied, running.State);
        Assert.Empty(closer.GracefulCalls); // nothing to restart: the flags are already ours
        Assert.Single(_started);
        Assert.Contains("activates the existing window", ReadLog());
    }

    // FileLog keeps the file open for writing, so read with ReadWrite sharing
    private string ReadLog()
    {
        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FakeChain : IChainSupervisor
    {
        public int EnsureCalls { get; private set; }

        public ChainException? Failure { get; set; }

        public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.FromResult(new ChainState(
            DaemonHttpListening: true, DaemonProcessAlive: true, KeeperRunning: true,
            BackendState: "Running", ProxyRunning: true, ProxyHealthy: true));

        public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct)
        {
            EnsureCalls++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeProcessFinder : IProcessFinder
    {
        public IReadOnlyList<FoundProcess> Processes { get; set; } = [];

        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) => Processes;

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => Processes;

        public FoundProcess? FindById(int processId) => null;
    }

    public void Dispose()
    {
        _log.Dispose();
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }
}
