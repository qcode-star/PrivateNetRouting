using System.Net;
using System.Net.Sockets;
using System.Text;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Tests;

// ----- diagnostics chain-link fakes -----

internal sealed class FakeTcpTable : ITcpConnectionTable
{
    public Dictionary<int, List<TcpListenerEntry>> Listeners { get; } = [];

    public IReadOnlyList<TcpListenerEntry> GetListeners(int port) =>
        Listeners.TryGetValue(port, out var entries) ? entries : [];
}

internal sealed class FakeProxyStats : IProxyStatistics
{
    public bool IsRunning { get; set; }

    public IReadOnlyList<string> ListenerEndpoints { get; set; } = [];

    public int ActiveConnections { get; set; }

    public long TotalConnections { get; set; }

    public long FailedConnections { get; set; }
}

internal sealed class ThrowingWslRunner : IWslProcessRunner
{
    public Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, Encoding outputEncoding, CancellationToken ct) =>
        throw new InvalidOperationException("WSL must not be touched by the diagnostics core status");
}

public class DiagnosticsTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-diag-").FullName;

    // ----- summary: stopped-chain edge case -----

    [Fact]
    public async Task StoppedChainIsAShortNotice()
    {
        var service = MakeService(out _, out var proxy, out var table, out _, out _, out _);
        proxy.IsRunning = false;
        table.Listeners.Clear(); // neither daemon nor forward-proxy listener

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.True(summary.ChainStopped);
        Assert.Empty(summary.Sections);
    }

    [Fact]
    public async Task ForeignProxyHolderIsNotAStoppedChain()
    {
        var service = MakeService(out var config, out var proxy, out var table, out _, out _, out _);
        proxy.IsRunning = false;
        var port = ParsePort(config.ForwardProxy.Listen);
        table.Listeners[port] = [new TcpListenerEntry(IPAddress.Loopback, port, 4242, "PrivateNetRouting.App")];

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.False(summary.ChainStopped);
        var line = Section(summary, "proxy").Single(l => l.Name == "forward proxy");
        Assert.Contains("pid 4242 (PrivateNetRouting.App)", line.Value);
        Assert.Contains("another process", line.Value);
    }

    // ----- summary: full chain -----

    [Fact]
    public async Task FullChainShowsOwnersKeeperNodeMarkerAndGuards()
    {
        var marker = Path.Combine(_tempDir, "marker");
        File.WriteAllText(marker, string.Empty);

        var service = MakeService(out var config, out var proxy, out var table, out var finder, out var probe, out var guardCore);
        var daemonHttp = ParsePort(config.Chain.Tailscale.HttpListen);
        var daemonSocks = ParsePort(config.Chain.Tailscale.SocksListen);
        var proxyPort = ParsePort(config.ForwardProxy.Listen);
        config.ForwardProxy.ActivityMarkerPath = marker;
        proxy.IsRunning = true;
        proxy.ListenerEndpoints = ["127.0.0.1:18082", "192.0.2.1:18082"];
        proxy.ActiveConnections = 2;
        proxy.TotalConnections = 145;
        proxy.FailedConnections = 3;
        table.Listeners[daemonHttp] = [new TcpListenerEntry(IPAddress.Loopback, daemonHttp, 100, "tailscaled")];
        table.Listeners[daemonSocks] = [new TcpListenerEntry(IPAddress.Loopback, daemonSocks, 100, "tailscaled")];
        table.Listeners[proxyPort] =
        [
            new TcpListenerEntry(IPAddress.Loopback, proxyPort, Environment.ProcessId, "testhost"),
            new TcpListenerEntry(IPAddress.Parse("192.0.2.1"), proxyPort, Environment.ProcessId, "testhost"),
        ];
        finder.ByName["tailscale.exe"] = [new FoundProcess(101, $"tailscale --socket={config.Chain.Tailscale.Socket} debug watch-ipn")];
        finder.ByName["pnr.exe"] = [new FoundProcess(202, "pnr watchdog")];
        probe.Result = HealthProbeResult.OkIn(320, "HTTP/1.1 200 Connection Established");
        guardCore.Status = (
            new LocalBanStatus(GuardState.On, 38, true, 1, ["claude.ai", "openai.com"], 3, ["160.79.104.0/21"]),
            new EgressPolicyStatus(GuardState.On, "http://127.0.0.1:18082", "http://127.0.0.1:18082", 0));

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.False(summary.ChainStopped);
        Assert.Equal(["chain", "proxy", "health", "guards", "watchdog", "logs"], summary.Sections.Select(s => s.Key));

        var chain = Section(summary, "chain");
        Assert.Contains("pid 100 (tailscaled)", chain.Single(l => l.Name.StartsWith("daemon")).Value);
        Assert.Equal("pid 101", chain.Single(l => l.Name == "keeper watch-ipn").Value);
        Assert.Equal("Running", chain.Single(l => l.Name == "node").Value);

        var proxySection = Section(summary, "proxy");
        Assert.Equal($"running (pid {Environment.ProcessId})", proxySection.Single(l => l.Name == "forward proxy").Value);
        Assert.Equal("192.0.2.1", proxySection.Single(l => l.Name == "WSL bind").Value);
        Assert.Contains("active 2, total 145, failed 3", proxySection.Single(l => l.Name == "connections").Value);
        Assert.EndsWith("s ago", proxySection.Single(l => l.Name == "activity").Value);

        var health = Section(summary, "health");
        Assert.Contains("OK in 320 ms", health.Single(l => l.Name.StartsWith("CONNECT-roundtrip")).Value);

        var guards = Section(summary, "guards");
        Assert.Contains("egressProxyUrl=http://127.0.0.1:18082", guards.Single(l => l.Name == "egress policy").Value);
        Assert.Equal(DiagMark.Good, guards.Single(l => l.Name == "egress policy").Mark);
        Assert.Contains("hosts: 38 names", guards.Single(l => l.Name == "local ban").Value);
        Assert.Equal(DiagMark.Good, guards.Single(l => l.Name == "local ban").Mark);

        Assert.Equal("running (pid 202)", Section(summary, "watchdog").Single(l => l.Name == "idle supervisor").Value);
        Assert.Contains(_tempDir, Section(summary, "logs").Single(l => l.Name == "logs").Value);
    }

    [Fact]
    public async Task FailedHealthAndMissingKeeperAreMarked()
    {
        var service = MakeService(out var config, out var proxy, out var table, out _, out var probe, out _);
        var proxyPort = ParsePort(config.ForwardProxy.Listen);
        table.Listeners[proxyPort] = [new TcpListenerEntry(IPAddress.Loopback, proxyPort, Environment.ProcessId, "testhost")];
        proxy.IsRunning = true;
        probe.Result = HealthProbeResult.FailIn(150, "connection refused");
        config.ForwardProxy.ActivityMarkerPath = Path.Combine(_tempDir, "absent-marker");

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        var health = Section(summary, "health").Single(l => l.Name.StartsWith("CONNECT-roundtrip"));
        Assert.Equal(DiagMark.Bad, health.Mark);
        Assert.Contains("failed in 150 ms: connection refused", health.Value);

        var keeper = Section(summary, "chain").Single(l => l.Name == "keeper watch-ipn");
        Assert.Equal(DiagMark.Warn, keeper.Mark);
        Assert.Contains("not found", keeper.Value);
        Assert.Equal("no marker yet (no proxied requests)", Section(summary, "proxy").Single(l => l.Name == "activity").Value);
    }

    [Fact]
    public async Task HealthIsSkippedWhenProxyDoesNotListen()
    {
        var service = MakeService(out var config, out _, out var table, out _, out var probe, out _);
        table.Listeners[ParsePort(config.Chain.Tailscale.HttpListen)] =
            [new TcpListenerEntry(IPAddress.Loopback, ParsePort(config.Chain.Tailscale.HttpListen), 100, "tailscaled")];
        probe.Result = HealthProbeResult.FailIn(0, "must not be called");

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal("skipped: proxy is not listening", Section(summary, "health").Single().Value);
    }

    // ----- socks5 mode: the chain section is one egress line -----

    [Fact]
    public async Task Socks5ModeShowsEgressInsteadOfTailscaleLinks()
    {
        var service = MakeService(out var config, out var proxy, out var table, out _, out var probe, out _,
            mode: "socks5", upstreamReachable: true);
        config.Chain.Socks5.User = "alice";
        var proxyPort = ParsePort(config.ForwardProxy.Listen);
        table.Listeners[proxyPort] = [new TcpListenerEntry(IPAddress.Loopback, proxyPort, Environment.ProcessId, "testhost")];
        proxy.IsRunning = true;
        probe.Result = HealthProbeResult.OkIn(120, "HTTP/1.1 200 Connection Established");

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.False(summary.ChainStopped);
        Assert.Equal(["chain", "proxy", "health", "guards", "watchdog", "logs"], summary.Sections.Select(s => s.Key));

        var egress = Section(summary, "chain").Single();
        Assert.Equal($"egress (alice@{config.Chain.Socks5.Upstream})", egress.Name);
        Assert.Equal("reachable", egress.Value);
        Assert.Equal(DiagMark.Good, egress.Mark);
    }

    [Fact]
    public async Task Socks5UnreachableEgressIsMarkedBad()
    {
        var service = MakeService(out var config, out var proxy, out var table, out _, out _, out _,
            mode: "socks5", upstreamReachable: false);
        var proxyPort = ParsePort(config.ForwardProxy.Listen);
        table.Listeners[proxyPort] = [new TcpListenerEntry(IPAddress.Loopback, proxyPort, Environment.ProcessId, "testhost")];
        proxy.IsRunning = true;

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        var egress = Section(summary, "chain").Single();
        Assert.Equal($"egress ({config.Chain.Socks5.Upstream})", egress.Name);
        Assert.Equal("no route", egress.Value);
        Assert.Equal(DiagMark.Bad, egress.Mark);
    }

    [Fact]
    public async Task Socks5StoppedChainIgnoresForeignDaemonListeners()
    {
        var service = MakeService(out var config, out var proxy, out var table, out _, out _, out _, mode: "socks5");
        proxy.IsRunning = false;
        var daemonHttp = ParsePort(config.Chain.Tailscale.HttpListen);
        table.Listeners[daemonHttp] = [new TcpListenerEntry(IPAddress.Loopback, daemonHttp, 100, "tailscaled")];

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.True(summary.ChainStopped); // a daemon listener means nothing in the socks5 mode
        Assert.Empty(summary.Sections);
    }

    // ----- socks5 mode, the local proxy skipped (forwardProxy.enabled=false) -----

    [Fact]
    public async Task SkippedShapeShowsTheExternalEndpointAndRunsHealth()
    {
        var service = MakeService(out var config, out var proxy, out var table, out _, out var probe, out _,
            mode: "socks5", upstreamReachable: true, skipProxy: true);
        config.Chain.Socks5.Upstream = "192.0.2.10:7080";
        proxy.IsRunning = false; // no local proxy exists in this shape
        // even a foreign listener on the forward-proxy port means nothing: nobody of ours is there
        var forwardProxyPort = ParsePort(config.ForwardProxy.Listen);
        table.Listeners[forwardProxyPort] = [new TcpListenerEntry(IPAddress.Loopback, forwardProxyPort, 100, "sing-box")];
        probe.Result = HealthProbeResult.OkIn(90, "HTTP/1.1 200 Connection Established");

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.False(summary.ChainStopped); // the egress answers — the chain is up
        Assert.Equal(["chain", "proxy", "health", "guards", "watchdog", "logs"], summary.Sections.Select(s => s.Key));

        var proxySection = Section(summary, "proxy");
        Assert.Equal(
            "skipped — the applications point at 192.0.2.10:7080",
            proxySection.Single(l => l.Name == "forward proxy").Value);
        Assert.Equal("-", proxySection.Single(l => l.Name == "connections").Value);
        Assert.Equal("not kept (the local proxy is skipped)", proxySection.Single(l => l.Name == "activity").Value);

        var health = Section(summary, "health").Single(l => l.Name.StartsWith("CONNECT-roundtrip"));
        Assert.Contains("OK in 90 ms", health.Value); // the probe runs against the external endpoint
    }

    [Fact]
    public async Task SkippedShapeWithDeadEgressIsAStoppedChain()
    {
        var service = MakeService(out var config, out var proxy, out var table, out _, out _, out _,
            mode: "socks5", upstreamReachable: false, skipProxy: true);
        proxy.IsRunning = false;
        var forwardProxyPort = ParsePort(config.ForwardProxy.Listen);
        table.Listeners[forwardProxyPort] = [new TcpListenerEntry(IPAddress.Loopback, forwardProxyPort, 100, "sing-box")];

        var summary = await service.GetSummaryAsync(CancellationToken.None);

        Assert.True(summary.ChainStopped); // the port is foreign and the egress is down — nothing is up
        Assert.Empty(summary.Sections);
    }

    // ----- guard core for diagnostics: without settings.json and WSL -----

    [Fact]
    public async Task GuardCoreStatusSkipsSettingsAndWsl()
    {
        var config = new AppConfig();
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts");
        File.WriteAllText(config.Guard.HostsFile, "127.0.0.1 localhost\r\n");
        HostsFileBlock.Apply(
            config.Guard.HostsFile,
            config.Guard.HostsBeginMarker,
            config.Guard.HostsEndMarker,
            config.Guard.HostsComment,
            ["claude.ai"]);
        var banOps = new FakeBanSystemOps
        {
            Layers = new BanLayersState(1, ["claude.ai"], 3, ["160.79.104.0/21"]),
        };
        var policy = new FakeEgressPolicyStore
        {
            Values = new EgressPolicyValues(true, ClaudeEnvValues.WindowsProxyUrl(config), 0),
        };
        var log = new FileLog(Path.Combine(_tempDir, $"guard-{Guid.NewGuid():N}.log"));
        var guard = new GuardService(
            config,
            policy,
            banOps,
            new ClaudeSettingsGuard(config),
            new CodexSettingsGuard(config),
            new WslConfigGuard(config, new ThrowingWslRunner(), () => IPAddress.Parse("192.0.2.1")),
            new FakeSystemProxyStore(),
            new FakeElevatedRunner(),
            log,
            _ => Task.FromResult(false));

        var (ban, policyStatus) = await guard.GetCoreStatusAsync(CancellationToken.None);

        Assert.Equal(GuardState.On, ban.State);
        Assert.Equal(1, ban.HostsEntries);
        Assert.Equal(GuardState.On, policyStatus.State);
        Assert.Equal("status", Assert.Single(banOps.Calls));
    }

    // ----- detailed CONNECT-roundtrip probe -----

    [Fact]
    public async Task ProbeReturnsTimingAndStatusLine()
    {
        var (port, server) = await StartResponder("HTTP/1.1 200 Connection Established\r\n\r\n");
        try
        {
            var probe = await ProxyHealthCheck.ProbeAsync("127.0.0.1", port, "api.example", 443, TimeSpan.FromSeconds(5));
            Assert.True(probe.Ok);
            Assert.StartsWith("HTTP/1.1 200", probe.Detail);
            Assert.True(probe.Milliseconds >= 0);
        }
        finally
        {
            await server;
        }
    }

    [Fact]
    public async Task ProbeReportsNon2xxAndRefused()
    {
        var (port, server) = await StartResponder("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n");
        try
        {
            var probe = await ProxyHealthCheck.ProbeAsync("127.0.0.1", port, "api.example", 443, TimeSpan.FromSeconds(5));
            Assert.False(probe.Ok);
            Assert.StartsWith("HTTP/1.1 407", probe.Detail);
        }
        finally
        {
            await server;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var freePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var refused = await ProxyHealthCheck.ProbeAsync("127.0.0.1", freePort, "api.example", 443, TimeSpan.FromSeconds(5));
        Assert.False(refused.Ok);
        Assert.False(string.IsNullOrEmpty(refused.Detail));
    }

    private static async Task<(int Port, Task Server)> StartResponder(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            await Task.Delay(100); // give the client time to read the response before closing
        });
        return (port, server);
    }

    // ----- helpers -----

    private DiagnosticsService MakeService(
        out AppConfig config,
        out FakeProxyStats proxy,
        out FakeTcpTable table,
        out FakeFinderByName finder,
        out FakeProbe probe,
        out FakeGuardCore guardCore,
        string? mode = null,
        bool? upstreamReachable = null,
        bool skipProxy = false)
    {
        config = new AppConfig();
        if (mode is not null)
        {
            config.Chain.Mode = mode;
        }

        config.ForwardProxy.Listen = "127.0.0.1:18082";
        config.ForwardProxy.Enabled = !skipProxy;
        config.Chain.Tailscale.HttpListen = "127.0.0.1:18081";
        config.Chain.Tailscale.SocksListen = "127.0.0.1:18083";
        config.ForwardProxy.ActivityMarkerPath = Path.Combine(_tempDir, "absent-marker");
        config.Logging.Directory = _tempDir;
        proxy = new FakeProxyStats();
        table = new FakeTcpTable();
        finder = new FakeFinderByName();
        probe = new FakeProbe();
        guardCore = new FakeGuardCore();
        var probeFixture = probe;
        var guardFixture = guardCore;
        var state = mode == "socks5"
            ? skipProxy
                ? new ChainState(false, false, false, null, null, upstreamReachable == true, upstreamReachable)
                : new ChainState(false, false, false, null, proxy.IsRunning, true, upstreamReachable)
            : new ChainState(true, true, true, "Running", proxy.IsRunning, true);
        return new DiagnosticsService(
            config,
            new FakeDiagChain(state),
            proxy,
            table,
            finder,
            ct => probeFixture.Next(ct),
            ct => guardFixture.Next(ct),
            new LogStore(_tempDir));
    }

    private static int ParsePort(string listen) => IPEndPoint.Parse(listen).Port;

    private static IReadOnlyList<DiagLine> Section(DiagnosticsSummary summary, string key) =>
        summary.Sections.Single(s => s.Key == key).Lines;

    private sealed class FakeDiagChain(ChainState state) : IChainSupervisor
    {
        public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.FromResult(state);

        public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeProbe
    {
        public HealthProbeResult Result { get; set; } = HealthProbeResult.OkIn(1, "HTTP/1.1 200 ok");

        public Task<HealthProbeResult> Next(CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class FakeGuardCore
    {
        public (LocalBanStatus, EgressPolicyStatus) Status { get; set; } =
            (LocalBanStatus.Off, new EgressPolicyStatus(GuardState.Off, null, "http://127.0.0.1:18082", null));

        public Task<(LocalBanStatus Ban, EgressPolicyStatus Policy)> Next(CancellationToken ct) =>
            Task.FromResult(Status);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
