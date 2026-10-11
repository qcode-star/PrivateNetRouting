using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Browser launcher: bypass lists (Chromium and Firefox), user.js, Firefox and Chromium
/// command lines, the "check → up → launch" order, modes, exe path fallback, "already running".
/// </summary>
public class BrowserLauncherTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-browser-{Guid.NewGuid():N}.log");
    private readonly FileLog _log;
    private readonly FakeChain _chain = new();
    private readonly FakeProcessFinder _processes = new();
    private readonly List<ProcessStartInfo> _started = [];

    public BrowserLauncherTests()
    {
        _log = new FileLog(_logPath);
    }

    private static AppConfig NewConfig(BrowserKind kind = BrowserKind.Firefox) => new()
    {
        ForwardProxy = new ForwardProxyConfig { Listen = "127.0.0.1:18082" },
        Launcher = new LauncherConfig { BypassDomains = ["corp.example.com", ".team.example.net"] },
        Browser = new BrowserConfig
        {
            Type = kind == BrowserKind.Chromium ? "chromium" : "firefox",
            DirectTlds = ["ru", "by"],
            Firefox = new BrowserFirefoxConfig
            {
                Path = @"C:\fake\firefox.exe",
                ProfileDir = Path.Combine(AppContext.BaseDirectory, "pnr-test-ff-profile"),
            },
            Chromium = new BrowserChromiumConfig
            {
                Path = @"C:\fake\msedge.exe",
                ProfileDir = Path.Combine(AppContext.BaseDirectory, "pnr-test-chromium-profile"),
            },
        },
    };

    private BrowserLauncher Create(
        AppConfig? config = null,
        bool healthy = true,
        ChainException? chainFailure = null,
        Func<string, bool>? fileExists = null)
    {
        config ??= NewConfig();
        _chain.Failure = chainFailure;
        return new BrowserLauncher(
            config,
            _chain,
            _ => Task.FromResult(healthy),
            _processes,
            _log,
            fileExists ?? (_ => true),
            psi =>
            {
                _started.Add(psi);
                return null;
            });
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

    [Fact]
    public void UserJsCarriesProxyAndAllPrefsOfTheOldScheme()
    {
        var js = FirefoxUserJs.Render("127.0.0.1", 18082, "localhost, *.ru");

        Assert.Contains(@"user_pref(""network.proxy.type"", 1);", js);
        Assert.Contains(@"user_pref(""network.proxy.http"", ""127.0.0.1"");", js);
        Assert.Contains(@"user_pref(""network.proxy.http_port"", 18082);", js);
        Assert.Contains(@"user_pref(""network.proxy.ssl"", ""127.0.0.1"");", js);
        Assert.Contains(@"user_pref(""network.proxy.ssl_port"", 18082);", js);
        Assert.Contains(@"user_pref(""network.proxy.share_proxy_settings"", true);", js);
        Assert.Contains(@"user_pref(""network.proxy.failover_direct"", false);", js);
        Assert.Contains(@"user_pref(""network.proxy.no_proxies_on"", ""localhost, *.ru"");", js);
        Assert.Contains(@"user_pref(""network.trr.mode"", 5);", js);
        Assert.Contains(@"user_pref(""browser.shell.checkDefaultBrowser"", false);", js);
        Assert.Contains(@"user_pref(""datareporting.policy.dataSubmissionEnabled"", false);", js);
        // WebRTC goes around the proxy and reveals the real address: off unless the owner lifts it
        Assert.Contains(@"user_pref(""media.peerconnection.enabled"", false);", js);
        Assert.Contains(@"user_pref(""media.navigator.enabled"", false);", js);
    }

    [Fact]
    public void UserJsLeavesWebRtcAloneWhenTheConfigAllowsIt()
    {
        var js = FirefoxUserJs.Render("127.0.0.1", 18082, "localhost, *.ru", disableWebRtc: false);

        Assert.DoesNotContain("peerconnection", js);
        Assert.DoesNotContain("media.navigator", js);
        Assert.Contains(@"user_pref(""network.trr.mode"", 5);", js);
    }

    [Fact]
    public async Task HealthyChainLaunchesFirefoxWithProfileAndNewInstance()
    {
        var config = NewConfig();
        var launcher = Create(config);

        var result = await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
        Assert.False(result.ChainRaised);
        Assert.Equal(0, _chain.EnsureCalls);
        var psi = Assert.Single(_started);
        Assert.Equal(@"C:\fake\firefox.exe", psi.FileName);
        Assert.Equal(
            ["-profile", config.Browser.Firefox.ProfileDir, "--new-instance", "https://claude.ai"],
            psi.ArgumentList);
        // user.js is rewritten on every launch, with the proxy address from the app config
        var userJs = await File.ReadAllTextAsync(Path.Combine(config.Browser.Firefox.ProfileDir, "user.js"));
        Assert.Contains(@"user_pref(""network.proxy.http_port"", 18082);", userJs);
        Assert.Contains("team.example.net", userJs);
        Assert.Contains("*.ru", userJs);
        Assert.Contains(@"user_pref(""media.peerconnection.enabled"", false);", userJs);
        Assert.Contains("WebRTC off", ReadLog());
    }

    [Fact]
    public async Task ConfiguredWebRtcIsNotSwitchedOffInUserJs()
    {
        var config = NewConfig();
        config.Browser.DisableWebRtc = false;
        var launcher = Create(config);

        await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

        var userJs = await File.ReadAllTextAsync(Path.Combine(config.Browser.Firefox.ProfileDir, "user.js"));
        Assert.DoesNotContain("peerconnection", userJs);
        Assert.Contains("WebRTC on", ReadLog());
    }

    [Fact]
    public async Task PrivateOptionOpensPrivateWindow()
    {
        var config = NewConfig();
        var launcher = Create(config);

        await launcher.LaunchAsync(new BrowserLauncherOptions(Private: true), null, CancellationToken.None);

        var psi = Assert.Single(_started);
        Assert.Equal(
            ["-profile", config.Browser.Firefox.ProfileDir, "--new-instance", "-private-window", "https://claude.ai"],
            psi.ArgumentList);
    }

    [Fact]
    public async Task ConfigPrivateWindowOpensPrivateWindowToo()
    {
        var config = NewConfig();
        config.Browser.Private = true;
        var launcher = Create(config);

        await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

        var psi = Assert.Single(_started);
        Assert.Contains("-private-window", psi.ArgumentList);
    }

    [Fact]
    public async Task UrlOptionOverridesConfig()
    {
        var launcher = Create(NewConfig());

        await launcher.LaunchAsync(new BrowserLauncherOptions(Url: "https://example.org"), null, CancellationToken.None);

        Assert.Equal("https://example.org", Assert.Single(_started).ArgumentList[^1]);
    }

    [Fact]
    public async Task ChromiumLaunchesWithProxyFlagsAndUserDataDir()
    {
        var config = NewConfig(BrowserKind.Chromium);
        var launcher = Create(config);

        var result = await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
        var psi = Assert.Single(_started);
        Assert.Equal(@"C:\fake\msedge.exe", psi.FileName);
        Assert.Equal(
            [
                "--proxy-server=http://127.0.0.1:18082",
                "--proxy-bypass-list=<local>;localhost;127.0.0.1;[::1];10.0.0.0/8;172.16.0.0/12;192.168.0.0/16;169.254.0.0/16;100.64.0.0/10;*.local;corp.example.com;*.corp.example.com;team.example.net;*.team.example.net;*.ru;*.by",
                // No full WebRTC switch exists in the family: the policy is the most it can be pinned down
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                $"--user-data-dir={config.Browser.Chromium.ProfileDir}",
                "--no-first-run",
                "--no-default-browser-check",
                "https://claude.ai",
            ],
            psi.ArgumentList);
        // Chromium has no user.js; the browser itself creates the profile on first launch
        Assert.False(File.Exists(Path.Combine(config.Browser.Chromium.ProfileDir, "user.js")));
    }

    [Fact]
    public async Task ConfiguredWebRtcLeavesTheChromiumPolicyOut()
    {
        var config = NewConfig(BrowserKind.Chromium);
        config.Browser.DisableWebRtc = false;
        var launcher = Create(config);

        await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

        Assert.DoesNotContain(
            Assert.Single(_started).ArgumentList,
            arg => arg.Contains("webrtc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChromiumPrivateAddsIncognito()
    {
        var config = NewConfig(BrowserKind.Chromium);
        var launcher = Create(config);

        await launcher.LaunchAsync(new BrowserLauncherOptions(Private: true), null, CancellationToken.None);

        var args = Assert.Single(_started).ArgumentList;
        Assert.Equal("--incognito", args[^2]);
        Assert.Equal("https://claude.ai", args[^1]);
    }

    [Fact]
    public async Task DeadChainIsRaisedBeforeBrowserLaunch()
    {
        var launcher = Create(healthy: false);

        var result = await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(BrowserLaunchOutcome.Launched, result.Outcome);
        Assert.True(result.ChainRaised);
        Assert.Equal(1, _chain.EnsureCalls);
        Assert.Single(_started);
    }

    [Fact]
    public async Task ChainFailureStopsLaunch()
    {
        var launcher = Create(healthy: false, chainFailure: new ChainException("daemon", new IOException("UAC declined")));

        await Assert.ThrowsAsync<ChainException>(() =>
            launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None));

        Assert.Empty(_started);
    }

    [Fact]
    public async Task CheckOnlyRaisesChainWithoutStartingBrowser()
    {
        var config = NewConfig();
        var launcher = Create(config, healthy: false);

        var result = await launcher.LaunchAsync(new BrowserLauncherOptions(CheckOnly: true), null, CancellationToken.None);

        Assert.Equal(BrowserLaunchOutcome.CheckOnly, result.Outcome);
        Assert.Equal(1, _chain.EnsureCalls);
        Assert.Empty(_started);
        Assert.False(File.Exists(Path.Combine(config.Browser.Firefox.ProfileDir, "user.js")));
    }

    [Fact]
    public async Task PrepareOnlyWritesUserJsButDoesNotStartBrowser()
    {
        var config = NewConfig();
        var launcher = Create(config);

        var result = await launcher.LaunchAsync(new BrowserLauncherOptions(PrepareOnly: true), null, CancellationToken.None);

        Assert.Equal(BrowserLaunchOutcome.Prepared, result.Outcome);
        Assert.Empty(_started);
        var userJs = await File.ReadAllTextAsync(Path.Combine(config.Browser.Firefox.ProfileDir, "user.js"));
        Assert.Contains(@"user_pref(""network.proxy.http_port"", 18082);", userJs);
    }

    [Fact]
    public async Task PrepareOnlyChromiumNeedsNoPreparation()
    {
        var config = NewConfig(BrowserKind.Chromium);
        var launcher = Create(config);

        var result = await launcher.LaunchAsync(new BrowserLauncherOptions(PrepareOnly: true), null, CancellationToken.None);

        Assert.Equal(BrowserLaunchOutcome.Prepared, result.Outcome);
        Assert.Empty(_started);
        Assert.False(Directory.Exists(config.Browser.Chromium.ProfileDir));
    }

    [Fact]
    public async Task MissingExecutableFallsBackToSecondStandardPath()
    {
        var fallback = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe");
        var launcher = Create(fileExists: path => path == fallback);

        await launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(fallback, Assert.Single(_started).FileName);
        Assert.Contains("using fallback", ReadLog());
    }

    [Fact]
    public async Task MissingEverywhereFailsWithClearError()
    {
        var launcher = Create(fileExists: _ => false);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            launcher.LaunchAsync(BrowserLauncherOptions.Default, null, CancellationToken.None));

        Assert.Contains("browser executable not found", error.Message);
        Assert.Empty(_started);
    }

    [Fact]
    public void IsOurBrowserRunningMatchesProfileDir()
    {
        var config = NewConfig();
        var launcher = Create(config);

        Assert.False(launcher.IsOurBrowserRunning());

        _processes.Processes = [new FoundProcess(1, $"firefox.exe -profile \"{config.Browser.Firefox.ProfileDir}\" --new-instance https://claude.ai")];
        Assert.True(launcher.IsOurBrowserRunning());

        _processes.Processes = [new FoundProcess(2, "firefox.exe -profile \"C:\\daily\\profile\" https://claude.ai")];
        Assert.False(launcher.IsOurBrowserRunning());
    }

    [Fact]
    public void IsOurBrowserRunningMatchesChromiumUserDataDir()
    {
        var config = NewConfig(BrowserKind.Chromium);
        var launcher = Create(config);

        _processes.Processes = [new FoundProcess(3, $"msedge.exe --user-data-dir={config.Browser.Chromium.ProfileDir} --no-first-run")];
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

        foreach (var profile in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "pnr-test-ff-profile"),
                     Path.Combine(AppContext.BaseDirectory, "pnr-test-chromium-profile"),
                 })
        {
            if (Directory.Exists(profile))
            {
                Directory.Delete(profile, recursive: true);
            }
        }
    }
}
