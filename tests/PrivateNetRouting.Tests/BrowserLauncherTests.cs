using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Browser launcher: bypass lists (Chromium and Firefox), the egress probe, the launch command
/// lines of both families, the dead-egress contract (question, chain raise in the tailscale mode,
/// plain proceed over an external upstream), check-only and "already running".
/// </summary>
public class BrowserLauncherTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-browser-{Guid.NewGuid():N}.log");
    private readonly string _profileDir = Path.Combine(Path.GetTempPath(), $"pnr-test-profile-{Guid.NewGuid():N}");
    private readonly FileLog _log;
    private readonly FakeChain _chain = new();
    private readonly FakeProcessFinder _processes = new();
    private readonly List<ProcessStartInfo> _started = [];

    public BrowserLauncherTests()
    {
        _log = new FileLog(_logPath);
    }

    private static AppConfig NewConfig(BrowserKind kind = BrowserKind.Firefox, int egressPort = 18083) => new()
    {
        ForwardProxy = new ForwardProxyConfig { Listen = "127.0.0.1:18081" },
        Chain = new ChainConfig
        {
            Tailscale = new ChainTailscaleConfig { SocksListen = $"127.0.0.1:{egressPort}" },
            Socks5 = new ChainSocks5Config { Upstream = "127.0.0.1:18099" },
        },
        BypassDomains = ["corp.example.com", ".team.example.net"],
        DirectTlds = ["ru", "by"],
        Browser = new BrowserConfig
        {
            Type = kind == BrowserKind.Chrome ? "chrome" : "firefox",
            // the chain egress: the forward-proxy default has its own tests
            Egress = "chain",
        },
    };

    private AppConfig ConfiguredConfig(BrowserKind kind = BrowserKind.Firefox, int egressPort = 18083)
    {
        var config = NewConfig(kind, egressPort);
        config.Browser.ExecutablePath = @"C:\fake\browser.exe";
        config.Browser.ProfileDir = _profileDir;
        return config;
    }

    private BrowserLauncher Create(AppConfig config, ChainException? chainFailure = null)
    {
        _chain.Failure = chainFailure;
        return new BrowserLauncher(
            config,
            _chain,
            _log,
            _processes,
            psi =>
            {
                _started.Add(psi);
                return null;
            },
            path => path == config.Browser.ExecutablePath);
    }

    /// <summary>A live listener on an ephemeral port: the probe of a ready egress.</summary>
    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    [Fact]
    public void ChromiumBypassListHasTldsAndBothDomainForms()
    {
        var list = BrowserBypassList.BuildChromium(["corp.example.com"], ["ru", "by"]);

        Assert.Equal(
            "<local>;localhost;127.0.0.1;[::1];10.0.0.0/8;172.16.0.0/12;192.168.0.0/16;169.254.0.0/16;100.64.0.0/10;*.local"
            + ";corp.example.com;*.corp.example.com;*.ru;*.by",
            list);
    }

    [Fact]
    public void FirefoxNoProxiesHasBothFormsAndTldsButNoCidr()
    {
        var list = BrowserBypassList.BuildFirefoxNoProxies(["corp.example.com", ".team.example.net"], ["ru", "by"]);

        Assert.Equal(
            "localhost, 127.0.0.1, ::1, *.local, corp.example.com, .corp.example.com, team.example.net, .team.example.net, *.ru, *.by",
            list);
        Assert.DoesNotContain("/", list); // Firefox does not understand CIDR, so private ranges are absent from the list
    }

    [Fact]
    public void TldsAreTrimmedOfDotsAndStars()
    {
        var list = BrowserBypassList.BuildFirefoxNoProxies([], [".ru", "*.su"]);

        Assert.EndsWith("*.ru, *.su", list);
    }

    // ----- the egress the profile goes through -----

    [Fact]
    public void ChainEgressFollowsTheModeAndIsOursButForTheExternalUpstream()
    {
        var config = ConfiguredConfig();
        Assert.Equal(config.Chain.Tailscale.SocksListen, BrowserEgress.Resolve(config));
        Assert.Equal(BrowserEgress.Socks5Scheme, BrowserEgress.Scheme(config));
        Assert.True(Create(config).ChainOwnsEgress); // a dead one is fixed by a chain bring-up

        config.Chain.Mode = "socks5";
        Assert.Equal(config.Chain.Socks5.Upstream, BrowserEgress.Resolve(config));
        Assert.Equal(BrowserEgress.Socks5Scheme, BrowserEgress.Scheme(config));
        Assert.False(Create(config).ChainOwnsEgress); // external: there is nothing of ours to raise

        // the http mode's upstream speaks HTTP CONNECT itself: the chain egress is http, and the
        // names ride the CONNECT — the resolution warning of the tailscale mode does not apply
        config.Chain.Mode = "http";
        config.Chain.Http.Upstream = "192.0.2.20:3128";
        Assert.Equal("192.0.2.20:3128", BrowserEgress.Resolve(config));
        Assert.Equal(BrowserEgress.HttpScheme, BrowserEgress.Scheme(config));
        Assert.Equal("http://192.0.2.20:3128", BrowserEgress.ProxyUrl(config));
        Assert.False(Create(config).ChainOwnsEgress); // still external: nothing of ours to raise
    }

    [Fact]
    public void ForwardProxyIsTheDefaultEgressAndIsAlwaysOursToRaise()
    {
        var config = ConfiguredConfig();
        config.Browser.Egress = "forward-proxy"; // the default value itself is covered in AppConfigTests
        Assert.Equal(config.ForwardProxy.Listen, BrowserEgress.Resolve(config));
        Assert.Equal(BrowserEgress.HttpScheme, BrowserEgress.Scheme(config));
        Assert.Equal($"http://{config.ForwardProxy.Listen}", BrowserEgress.ProxyUrl(config));
        Assert.True(Create(config).ChainOwnsEgress); // the chain bring-up starts the proxy

        config.Chain.Mode = "socks5"; // the proxy is part of that bring-up too
        Assert.Equal(config.ForwardProxy.Listen, BrowserEgress.Resolve(config));
        Assert.True(Create(config).ChainOwnsEgress);

        // the http mode runs no local proxy: the egress is the provided HTTP upstream, not ours
        config.Chain.Mode = "http";
        config.Chain.Http.Upstream = "192.0.2.20:3128";
        Assert.Equal("192.0.2.20:3128", BrowserEgress.Resolve(config));
        Assert.False(Create(config).ChainOwnsEgress);
    }

    /// <summary>
    /// The skipped socks5 shape: the "forward-proxy" egress resolves to chain.socks5.upstream —
    /// still HTTP CONNECT, but not ours to raise (the dead-egress question is then "start
    /// anyway", like over the external upstream).
    /// </summary>
    [Fact]
    public void SkippedShapePointsTheForwardProxyEgressAtTheUpstream()
    {
        var config = ConfiguredConfig();
        config.Browser.Egress = "forward-proxy";
        config.Chain.Mode = "socks5";
        config.ForwardProxy.Enabled = false;
        config.Chain.Socks5.Upstream = "192.0.2.10:7080";

        Assert.Equal("192.0.2.10:7080", BrowserEgress.Resolve(config));
        Assert.Equal(BrowserEgress.HttpScheme, BrowserEgress.Scheme(config)); // the mixed inbound speaks CONNECT
        Assert.Equal("http://192.0.2.10:7080", BrowserEgress.ProxyUrl(config));
        Assert.False(Create(config).ChainOwnsEgress);
    }

    [Fact]
    public async Task EgressProbeSeesALiveListenerAndARefusedPort()
    {
        var listener = StartListener(out var port);
        try
        {
            var launcher = Create(ConfiguredConfig(egressPort: port));
            Assert.True(await launcher.IsEgressReachableAsync(CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }

        // nothing listens on the port any more: refused, and the launcher says so in the log
        var dead = Create(ConfiguredConfig(egressPort: port));
        Assert.False(await dead.IsEgressReachableAsync(CancellationToken.None));
        Assert.Contains("is not responding", ReadLog());
    }

    [Fact]
    public async Task EgressProbeHonoursCancellation()
    {
        var launcher = Create(ConfiguredConfig());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.IsEgressReachableAsync(cts.Token));
    }

    // ----- launch -----

    [Fact]
    public async Task ReadyEgressLaunchesFirefoxWithTheProfileAndNoUrl()
    {
        var listener = StartListener(out var port);
        var config = ConfiguredConfig(egressPort: port);
        try
        {
            var result = await Create(config)
                .LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

            Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
            Assert.True(result.EgressResponded);
            Assert.Equal(0, _chain.EnsureCalls); // a ready egress needs no bring-up
            var psi = Assert.Single(_started);
            Assert.Equal(@"C:\fake\browser.exe", psi.FileName);
            Assert.Equal(["-profile", _profileDir, "--new-instance"], psi.ArgumentList);

            // user.js is refreshed on every application launch: the egress address and the bypass list
            var userJs = await File.ReadAllTextAsync(Path.Combine(_profileDir, "user.js"));
            Assert.Contains(@"user_pref(""network.proxy.socks"", ""127.0.0.1"");", userJs);
            Assert.Contains($@"user_pref(""network.proxy.socks_port"", {port});", userJs);
            Assert.Contains("*.ru", userJs);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task PrivateAndUrlOptionsOnlyComeFromTheCaller()
    {
        var listener = StartListener(out var port);
        var config = ConfiguredConfig(egressPort: port);
        try
        {
            await Create(config).LaunchAsync(
                new BrowserLauncherOptions(Private: true, Url: "https://example.org"),
                null,
                CancellationToken.None);

            Assert.Equal(
                ["-profile", _profileDir, "--new-instance", "-private-window", "https://example.org"],
                Assert.Single(_started).ArgumentList);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ChromeLaunchesThroughTheEgressWithTheProfileFlags()
    {
        var listener = StartListener(out var port);
        var config = ConfiguredConfig(BrowserKind.Chrome, port);
        try
        {
            var result = await Create(config).LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

            Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
            var psi = Assert.Single(_started);
            Assert.Equal(
                [
                    $"--proxy-server=socks5://127.0.0.1:{port}",
                    "--proxy-bypass-list=<local>;localhost;127.0.0.1;[::1];10.0.0.0/8;172.16.0.0/12;192.168.0.0/16;169.254.0.0/16;100.64.0.0/10;*.local;corp.example.com;*.corp.example.com;team.example.net;*.team.example.net;*.ru;*.by",
                    // WebRTC is handled by the profile's policy alone: the command-line switch of
                    // the family is gone from current builds (verified live on Chrome 155)
                    $"--user-data-dir={_profileDir}",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--restore-last-session",
                ],
                psi.ArgumentList);

            // Chromium has no user.js; the profile files are written by the creation dialog alone
            Assert.False(File.Exists(Path.Combine(_profileDir, "user.js")));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ForwardProxyEgressLaunchesFirefoxOverHttpConnect()
    {
        var listener = StartListener(out var port);
        var config = ConfiguredConfig(egressPort: port);
        config.Browser.Egress = "forward-proxy";
        config.ForwardProxy.Listen = $"127.0.0.1:{port}"; // the default egress: the application's own proxy
        try
        {
            var result = await Create(config)
                .LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

            Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
            var userJs = await File.ReadAllTextAsync(Path.Combine(_profileDir, "user.js"));
            Assert.Contains($@"user_pref(""network.proxy.http_port"", {port});", userJs);
            Assert.Contains($@"user_pref(""network.proxy.ssl_port"", {port});", userJs);
            Assert.DoesNotContain("socks_remote_dns", userJs);
            Assert.DoesNotContain("resolves names via the system resolver", ReadLog());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ForwardProxyEgressGivesChromeAnHttpProxyServer()
    {
        var listener = StartListener(out var port);
        var config = ConfiguredConfig(BrowserKind.Chrome, port);
        config.Browser.Egress = "forward-proxy";
        config.ForwardProxy.Listen = $"127.0.0.1:{port}";
        try
        {
            await Create(config).LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

            var psi = Assert.Single(_started);
            Assert.Equal($"--proxy-server=http://127.0.0.1:{port}", psi.ArgumentList[0]);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ChainEgressInTailscaleWarnsAboutTheSystemResolver()
    {
        var listener = StartListener(out var port);
        try
        {
            // the sidecar's SOCKS5 resolves via the system resolver: the launch says so in the log
            var result = await Create(ConfiguredConfig(egressPort: port))
                .LaunchAsync(new BrowserLauncherOptions(CheckOnly: true), null, CancellationToken.None);

            Assert.Equal(BrowserLaunchOutcome.CheckOnly, result.Outcome);
            Assert.Contains("resolves names via the system resolver", ReadLog());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task UnconfiguredProfileRefusesToLaunch()
    {
        var config = NewConfig();
        config.Browser.ExecutablePath = "";
        config.Browser.ProfileDir = "";
        var launcher = Create(config);

        Assert.False(launcher.IsConfigured);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None));

        Assert.Contains("not created", error.Message);
        Assert.Empty(_started);
    }

    // ----- a dead egress -----

    [Fact]
    public async Task DeadEgressInTailscaleRaisesTheChainAfterAConfirmation()
    {
        var listener = StartListener(out var port);
        listener.Stop();
        var config = ConfiguredConfig(egressPort: port);
        var asked = 0;

        var result = await Create(config).LaunchAsync(
            BrowserLauncherOptions.Default with
            {
                ConfirmDeadEgress = _ =>
                {
                    asked++;
                    return Task.FromResult(true);
                },
            },
            null,
            CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.Equal(1, _chain.EnsureCalls); // the egress belongs to our chain: the answer raises it
        Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
        Assert.False(result.EgressResponded);
        Assert.Single(_started);
    }

    [Fact]
    public async Task EgressThatAnswersAfterTheRaiseIsReportedAsResponding()
    {
        var listener = StartListener(out var port);
        listener.Stop();
        var config = ConfiguredConfig(egressPort: port);
        TcpListener? revived = null;
        _chain.OnEnsure = () =>
        {
            revived = new TcpListener(IPAddress.Loopback, port);
            revived.Start();
        };

        try
        {
            var result = await Create(config).LaunchAsync(
                BrowserLauncherOptions.Default with { ConfirmDeadEgress = _ => Task.FromResult(true) },
                null,
                CancellationToken.None);

            Assert.Equal(1, _chain.EnsureCalls); // the raise happens once, in the launcher only
            Assert.True(result.EgressResponded); // the verdict is re-taken after the raise
            Assert.Single(_started);
        }
        finally
        {
            revived?.Stop();
        }
    }

    [Fact]
    public async Task DeadEgressOverAnExternalUpstreamOnlyAsksAndLaunchesAnyway()
    {
        var listener = StartListener(out var port);
        listener.Stop();
        var config = ConfiguredConfig(BrowserKind.Chrome, port);
        config.Chain.Mode = "socks5";
        config.Chain.Socks5.Upstream = $"127.0.0.1:{port}"; // the dead port is the external upstream

        var result = await Create(config).LaunchAsync(
            BrowserLauncherOptions.Default with { ConfirmDeadEgress = _ => Task.FromResult(true) },
            null,
            CancellationToken.None);

        Assert.Equal(0, _chain.EnsureCalls); // nothing of ours is in that path
        Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
        Assert.False(result.EgressResponded);
        Assert.Single(_started);
    }

    [Fact]
    public async Task DeadForwardProxyEgressRaisesTheChainEvenInTheSocks5Mode()
    {
        var listener = StartListener(out var port);
        listener.Stop();
        var config = ConfiguredConfig(egressPort: port);
        config.Browser.Egress = "forward-proxy";
        config.ForwardProxy.Listen = $"127.0.0.1:{port}"; // the dead port is the application's proxy
        config.Chain.Mode = "socks5";

        var result = await Create(config).LaunchAsync(
            BrowserLauncherOptions.Default with { ConfirmDeadEgress = _ => Task.FromResult(true) },
            null,
            CancellationToken.None);

        Assert.Equal(1, _chain.EnsureCalls); // the chain bring-up starts the proxy
        Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
        Assert.Single(_started);
    }

    [Fact]
    public async Task RefusedQuestionOverADeadEgressStartsNothing()
    {
        var listener = StartListener(out var port);
        listener.Stop();

        var result = await Create(ConfiguredConfig(egressPort: port)).LaunchAsync(
            BrowserLauncherOptions.Default with { ConfirmDeadEgress = _ => Task.FromResult(false) },
            null,
            CancellationToken.None);

        Assert.Equal(BrowserLaunchOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, _chain.EnsureCalls);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task DeadEgressWithoutAQuestionFailsTheLaunch()
    {
        var listener = StartListener(out var port);
        listener.Stop();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            Create(ConfiguredConfig(egressPort: port))
                .LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None));

        Assert.Contains("not responding", error.Message);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task ChainFailureDuringTheRaiseReachesTheCaller()
    {
        var listener = StartListener(out var port);
        listener.Stop();
        var launcher = Create(
            ConfiguredConfig(egressPort: port),
            chainFailure: new ChainException("daemon", new IOException("UAC declined")));

        await Assert.ThrowsAsync<ChainException>(() => launcher.LaunchAsync(
            BrowserLauncherOptions.Default with { ConfirmDeadEgress = _ => Task.FromResult(true) },
            null,
            CancellationToken.None));

        Assert.Empty(_started);
    }

    [Fact]
    public async Task CheckOnlyReportsTheEgressWithoutStartingAnything()
    {
        var listener = StartListener(out var port);
        try
        {
            var config = ConfiguredConfig(egressPort: port);
            var result = await Create(config)
                .LaunchAsync(new BrowserLauncherOptions(CheckOnly: true), null, CancellationToken.None);

            Assert.Equal(BrowserLaunchOutcome.CheckOnly, result.Outcome);
            Assert.True(result.EgressResponded);
            Assert.Equal(0, _chain.EnsureCalls); // the chain is not touched: `pnr chain up` is the way
            Assert.Empty(_started);
            Assert.False(File.Exists(Path.Combine(_profileDir, "user.js")));
        }
        finally
        {
            listener.Stop();
        }
    }

    // ----- executable and "already running" -----

    [Fact]
    public async Task MissingConfiguredExecutableFallsBackToTheStandardPlaces()
    {
        var listener = StartListener(out var port);
        var config = ConfiguredConfig(egressPort: port);
        config.Browser.ExecutablePath = @"C:\gone\firefox.exe";
        var fallback = BrowserExecutableLocator.Candidates(BrowserKind.Firefox)[0];
        var launcher = new BrowserLauncher(
            config,
            _chain,
            _log,
            _processes,
            psi =>
            {
                _started.Add(psi);
                return null;
            },
            path => path == fallback);
        try
        {
            await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

            Assert.Equal(fallback, Assert.Single(_started).FileName);
            Assert.Contains("the configured executable is not available", ReadLog());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task MissingEverywhereFailsWithAClearError()
    {
        var listener = StartListener(out var port);
        var config = ConfiguredConfig(egressPort: port);
        var launcher = new BrowserLauncher(
            config,
            _chain,
            _log,
            _processes,
            psi =>
            {
                _started.Add(psi);
                return null;
            },
            _ => false);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None));

            Assert.Contains("browser executable not found", error.Message);
            Assert.Empty(_started);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void IsOurBrowserRunningMatchesTheProfileDirectory()
    {
        var config = ConfiguredConfig();
        config.Browser.ExecutablePath = @"C:\fake\firefox.exe";
        var launcher = Create(config);

        Assert.False(launcher.IsOurBrowserRunning());

        _processes.Processes = [new FoundProcess(1, $"firefox.exe -profile \"{_profileDir}\" --new-instance")];
        Assert.True(launcher.IsOurBrowserRunning());

        _processes.Processes = [new FoundProcess(2, @"firefox.exe -profile C:\daily\profile")];
        Assert.False(launcher.IsOurBrowserRunning());
    }

    [Fact]
    public void IsOurBrowserRunningIsFalseWithoutAProfile()
    {
        var config = NewConfig();
        config.Browser.ExecutablePath = "";
        config.Browser.ProfileDir = "";
        var launcher = Create(config);

        _processes.Processes = [new FoundProcess(3, "firefox.exe -profile C:\\somewhere")];

        Assert.False(launcher.IsOurBrowserRunning());
    }

    [Fact]
    public void IsOurBrowserRunningMatchesTheChromeUserDataDir()
    {
        var config = ConfiguredConfig(BrowserKind.Chrome);
        config.Browser.ExecutablePath = @"C:\fake\chrome.exe";
        var launcher = Create(config);

        _processes.Processes = [new FoundProcess(3, $"chrome.exe --user-data-dir={_profileDir} --no-first-run")];

        Assert.True(launcher.IsOurBrowserRunning());
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

        /// <summary>Runs inside the bring-up - a test brings the egress listener up here.</summary>
        public Action? OnEnsure { get; set; }

        public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.FromResult(new ChainState(
            DaemonHttpListening: true, DaemonProcessAlive: true, KeeperRunning: true,
            BackendState: "Running", ProxyRunning: true, ProxyHealthy: true));

        public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct)
        {
            EnsureCalls++;
            OnEnsure?.Invoke();
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

        if (Directory.Exists(_profileDir))
        {
            Directory.Delete(_profileDir, recursive: true);
        }
    }
}
