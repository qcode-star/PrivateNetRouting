using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Creation of the browser profile: the files of both families (Firefox user.js, the Chromium
/// WebRTC policy), the command line shared by the button and the desktop shortcut, the executable
/// lookup and the service that ties them to the config.
/// </summary>
public class BrowserProfileTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-bprofile-{Guid.NewGuid():N}.log");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pnr-bprofile-dir-{Guid.NewGuid():N}");
    private readonly string _desktop = Path.Combine(Path.GetTempPath(), $"pnr-bprofile-desktop-{Guid.NewGuid():N}");
    private readonly FileLog _log;
    private readonly FakeShortcuts _shortcuts = new();
    private readonly List<Action<AppConfig>> _persisted = [];

    public BrowserProfileTests()
    {
        _log = new FileLog(_logPath);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_desktop);
    }

    private static AppConfig NewConfig() => new()
    {
        ForwardProxy = new ForwardProxyConfig { Listen = "127.0.0.1:18081" },
        Chain = new ChainConfig
        {
            Tailscale = new ChainTailscaleConfig { SocksListen = "127.0.0.1:18083" },
            Socks5 = new ChainSocks5Config { Upstream = "127.0.0.1:18099" },
        },
        BypassDomains = ["corp.example.com"],
        DirectTlds = ["ru", "by"],
        // the chain egress: the forward-proxy one has its own tests below
        Browser = new BrowserConfig { Egress = "chain" },
    };

    private BrowserProfileService CreateService(AppConfig config, Func<string, bool>? fileExists = null) =>
        new(
            config,
            _shortcuts,
            _log,
            fileExists,
            mutate =>
            {
                _persisted.Add(mutate);
                mutate(config);
            },
            () => _desktop);

    private string ProfileDir(string name = "profile") => Path.Combine(_root, name);

    // ----- Firefox: user.js -----

    [Fact]
    public void FirefoxUserJsCarriesTheEgressRemoteDnsAndTheGuards()
    {
        var js = BrowserProfileFiles.RenderFirefoxUserJs(BrowserEgress.Socks5Scheme, "127.0.0.1", 18083, "localhost, *.ru");

        // the egress SOCKS5 for all protocols, names resolved on its side, no silent direct fallback
        Assert.Contains(@"user_pref(""network.proxy.type"", 1);", js);
        Assert.Contains(@"user_pref(""network.proxy.socks"", ""127.0.0.1"");", js);
        Assert.Contains(@"user_pref(""network.proxy.socks_port"", 18083);", js);
        Assert.Contains(@"user_pref(""network.proxy.socks_version"", 5);", js);
        Assert.Contains(@"user_pref(""network.proxy.socks_remote_dns"", true);", js);
        Assert.Contains(@"user_pref(""network.proxy.failover_direct"", false);", js);
        Assert.Contains(@"user_pref(""network.proxy.no_proxies_on"", ""localhost, *.ru"");", js);
        Assert.Contains(@"user_pref(""network.proxy.http"", """");", js);
        Assert.Contains(@"user_pref(""network.proxy.ssl"", """");", js);
        Assert.Contains(@"user_pref(""network.trr.mode"", 5);", js);

        // the tabs of the last session come back
        Assert.Contains(@"user_pref(""browser.startup.page"", 3);", js);

        // no startup offers
        Assert.Contains(@"user_pref(""browser.aboutwelcome.enabled"", false);", js);
        Assert.Contains(@"user_pref(""browser.shell.checkDefaultBrowser"", false);", js);
        Assert.Contains(@"user_pref(""browser.discovery.enabled"", false);", js);
        Assert.Contains(@"user_pref(""datareporting.policy.dataSubmissionEnabled"", false);", js);

        // WebRTC and the built-in VPN would go around the proxy and reveal the real address
        Assert.Contains(@"user_pref(""media.peerconnection.enabled"", false);", js);
        Assert.Contains(@"user_pref(""media.navigator.enabled"", false);", js);
        Assert.Contains(@"user_pref(""browser.ipProtection.enabled"", false);", js);
    }

    [Fact]
    public void FirefoxUserJsOfTheForwardProxyEgressSpeaksHttpConnect()
    {
        var js = BrowserProfileFiles.RenderFirefoxUserJs(BrowserEgress.HttpScheme, "127.0.0.1", 18081, "localhost, *.ru");

        // the application's CONNECT proxy for all protocols, names resolved on its side
        Assert.Contains(@"user_pref(""network.proxy.http"", ""127.0.0.1"");", js);
        Assert.Contains(@"user_pref(""network.proxy.http_port"", 18081);", js);
        Assert.Contains(@"user_pref(""network.proxy.ssl"", ""127.0.0.1"");", js);
        Assert.Contains(@"user_pref(""network.proxy.ssl_port"", 18081);", js);
        Assert.Contains(@"user_pref(""network.proxy.socks"", """");", js);
        Assert.Contains(@"user_pref(""network.proxy.socks_port"", 0);", js);
        Assert.DoesNotContain("socks_remote_dns", js);
        Assert.Contains(@"user_pref(""network.proxy.failover_direct"", false);", js);
        Assert.Contains(@"user_pref(""network.proxy.no_proxies_on"", ""localhost, *.ru"");", js);
        Assert.Contains(@"user_pref(""network.trr.mode"", 5);", js);
    }

    // ----- Chromium: the WebRTC policy in the profile -----

    [Fact]
    public void ChromiumSeedWritesTheWebRtcPolicy()
    {
        var dir = ProfileDir("chrome");

        BrowserProfileFiles.Create(BrowserKind.Chrome, dir, BrowserEgress.Socks5Scheme, "127.0.0.1", 18083, "localhost");

        var preferences = File.ReadAllText(Path.Combine(dir, BrowserProfileFiles.ChromiumPreferencesRelativePath));
        Assert.Contains("\"ip_handling_policy\":\"disable_non_proxied_udp\"", preferences);
        Assert.False(File.Exists(Path.Combine(dir, BrowserProfileFiles.FirefoxUserJsName)));
    }

    [Fact]
    public void ChromiumSeedKeepsTheKeysOfAnExistingProfile()
    {
        var dir = ProfileDir("chrome-used");
        var path = Path.Combine(dir, BrowserProfileFiles.ChromiumPreferencesRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"profile":{"exit_type":"Crashed"},"spellcheck":{"dictionaries":["en-US"]}}""");

        BrowserProfileFiles.Create(BrowserKind.Chrome, dir, BrowserEgress.Socks5Scheme, "127.0.0.1", 18083, "localhost");

        var preferences = File.ReadAllText(path);
        Assert.Contains("\"exit_type\":\"Crashed\"", preferences); // the browser's own keys survive
        Assert.Contains("en-US", preferences);
        Assert.Contains("disable_non_proxied_udp", preferences);
    }

    [Fact]
    public void ChromiumSeedSurvivesAnUnreadablePreferencesFile()
    {
        var dir = ProfileDir("chrome-broken");
        var path = Path.Combine(dir, BrowserProfileFiles.ChromiumPreferencesRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        BrowserProfileFiles.Create(BrowserKind.Chrome, dir, BrowserEgress.Socks5Scheme, "127.0.0.1", 18083, "localhost");

        Assert.Contains("disable_non_proxied_udp", File.ReadAllText(path));
    }

    [Fact]
    public void FirefoxCreateIsIdempotent()
    {
        var dir = ProfileDir("firefox");

        BrowserProfileFiles.Create(BrowserKind.Firefox, dir, BrowserEgress.Socks5Scheme, "127.0.0.1", 18083, "localhost");
        BrowserProfileFiles.Create(BrowserKind.Firefox, dir, BrowserEgress.Socks5Scheme, "127.0.0.1", 18084, "localhost");

        // the second pass refreshes the file instead of piling up: the egress of the profile follows the config
        var js = File.ReadAllText(Path.Combine(dir, BrowserProfileFiles.FirefoxUserJsName));
        Assert.Contains(@"user_pref(""network.proxy.socks_port"", 18084);", js);
        Assert.DoesNotContain("18083", js);
    }

    [Fact]
    public void FirefoxSeedAcceptsTheTermsOfUseForAFreshProfile()
    {
        var dir = ProfileDir("firefox-tou");

        BrowserProfileFiles.Create(BrowserKind.Firefox, dir, BrowserEgress.Socks5Scheme, "127.0.0.1", 18083, "localhost");

        // the welcome/terms page of a fresh profile is skipped; the value sits above any shipped
        // version, so the page does not come back after a browser update either
        var prefs = File.ReadAllText(Path.Combine(dir, BrowserProfileFiles.FirefoxPrefsJsName));
        Assert.Contains(
            $@"user_pref(""termsofuse.acceptedVersion"", {BrowserProfileFiles.FirefoxTermsOfUseVersion});",
            prefs);
        Assert.Contains(@"user_pref(""termsofuse.acceptedDate"", """, prefs);
        // user.js must NOT carry it: a value pinned there would override the browser at every start
        Assert.DoesNotContain(
            "termsofuse",
            File.ReadAllText(Path.Combine(dir, BrowserProfileFiles.FirefoxUserJsName)));
    }

    [Fact]
    public void FirefoxSeedNeverRewritesAnExistingPrefsJs()
    {
        var dir = ProfileDir("firefox-started");
        var prefsPath = Path.Combine(dir, BrowserProfileFiles.FirefoxPrefsJsName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(prefsPath, """
            // Mozilla User Preferences

            user_pref("termsofuse.acceptedVersion", 4);
            user_pref("browser.startup.homepage", "https://example.org");
            """);

        BrowserProfileFiles.Create(BrowserKind.Firefox, dir, BrowserEgress.Socks5Scheme, "127.0.0.1", 18083, "localhost");

        // the file belongs to the browser once it has started; only our user.js is refreshed
        var prefs = File.ReadAllText(prefsPath);
        Assert.Contains(@"user_pref(""termsofuse.acceptedVersion"", 4);", prefs);
        Assert.DoesNotContain("99", prefs);
        Assert.Contains("example.org", prefs);
    }

    // ----- the command line shared by the button and the shortcut -----

    [Fact]
    public void FirefoxCommandLineCarriesTheProfile()
    {
        var args = BrowserProfileCommandLine.Build(BrowserKind.Firefox, @"C:\profiles\ff", "socks5://127.0.0.1:18083", "bypass");

        Assert.Equal(["-profile", @"C:\profiles\ff", "--new-instance"], args);
        Assert.Equal(@"-profile C:\profiles\ff --new-instance", BrowserProfileCommandLine.ToCommandLine(args));
    }

    [Fact]
    public void ChromeCommandLineCarriesTheEgressTheProfileAndTheSession()
    {
        var args = BrowserProfileCommandLine.Build(BrowserKind.Chrome, @"C:\profiles\ch", "socks5://127.0.0.1:18083", "localhost;*.ru");

        Assert.Equal(
            [
                "--proxy-server=socks5://127.0.0.1:18083",
                "--proxy-bypass-list=localhost;*.ru",
                @"--user-data-dir=C:\profiles\ch",
                "--no-first-run",
                "--no-default-browser-check",
                "--restore-last-session",
            ],
            args);
    }

    [Fact]
    public void ChromeCommandLineCarriesTheSchemeOfTheEgress()
    {
        var args = BrowserProfileCommandLine.Build(BrowserKind.Chrome, @"C:\profiles\ch", "http://127.0.0.1:18081", "localhost;*.ru");

        Assert.Equal("--proxy-server=http://127.0.0.1:18081", args[0]);
    }

    [Fact]
    public void ShortcutCommandLineQuotesOnlyWhatHasSpaces()
    {
        var args = BrowserProfileCommandLine.Build(BrowserKind.Firefox, @"C:\my profiles\ff", "socks5://127.0.0.1:18083", "bypass");

        Assert.Equal("-profile \"C:\\my profiles\\ff\" --new-instance", BrowserProfileCommandLine.ToCommandLine(args));
    }

    [Fact]
    public void ShortcutCommandLineEscapesATrailingBackslashAndEmbeddedQuotes()
    {
        // the closing quote follows the trailing backslash: it must be doubled, or CommandLineToArgvW
        // reads it as an escaped quote and the argument swallows the next one
        Assert.Equal("\"C:\\my profiles\\ch\\\\\"", BrowserProfileCommandLine.ToCommandLine([@"C:\my profiles\ch\"]));
        Assert.Equal("\"a\\\"b\"", BrowserProfileCommandLine.ToCommandLine(["a\"b"]));
        Assert.Equal("plain", BrowserProfileCommandLine.ToCommandLine(["plain"]));
    }

    [Fact]
    public void TheProfileDirectoryNeverCarriesATrailingSeparator()
    {
        var args = BrowserProfileCommandLine.Build(BrowserKind.Chrome, @"C:\profiles\ch\", "http://127.0.0.1:18081", "bypass");

        Assert.Contains(@"--user-data-dir=C:\profiles\ch", args);
        Assert.DoesNotContain(args, arg => arg.EndsWith(@"ch\"));
    }

    // ----- the folder predicate: only our own profile or a fresh folder may be written -----

    [Fact]
    public void ARealChromiumUserDataDirIsRefused()
    {
        var dir = ProfileDir("chrome-user-data");
        Directory.CreateDirectory(Path.Combine(dir, "Default"));
        File.WriteAllText(Path.Combine(dir, "Local State"), "{}");
        File.WriteAllText(Path.Combine(dir, "Default", "Preferences"), "{}");

        Assert.Equal(BrowserProfileFolderKind.BrowserUserData, BrowserProfileFiles.ClassifyFolder(dir));
    }

    [Fact]
    public void AChromiumUserDataDirWithoutOurMarkerIsRefused()
    {
        var dir = ProfileDir("chrome-user-data");
        Directory.CreateDirectory(Path.Combine(dir, "Default"));
        File.WriteAllText(Path.Combine(dir, "Default", "Preferences"), "{}");

        Assert.Equal(BrowserProfileFolderKind.BrowserUserData, BrowserProfileFiles.ClassifyFolder(dir));
    }

    /// <summary>
    /// A Chromium profile folder itself (User Data\Default, User Data\Profile 1) is the everyday
    /// data too: Preferences sits at its root, and a Default/Profile N folder of a user-data-dir
    /// is refused even before the browser wrote its Preferences.
    /// </summary>
    [Fact]
    public void AChromiumProfileFolderInsideAUserDataDirIsRefused()
    {
        var userData = ProfileDir("chrome-everyday-user-data");
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "Local State"), "{}");
        var defaultProfile = Path.Combine(userData, "Default");
        Directory.CreateDirectory(defaultProfile);
        File.WriteAllText(Path.Combine(defaultProfile, "Preferences"), "{}");
        var secondProfile = Path.Combine(userData, "Profile 1");
        Directory.CreateDirectory(secondProfile);
        File.WriteAllText(Path.Combine(secondProfile, "Secure Preferences"), "{}");
        var bareProfile = Path.Combine(userData, "Profile 2");
        Directory.CreateDirectory(bareProfile);
        File.WriteAllText(Path.Combine(bareProfile, "Bookmarks"), "{}");
        var copied = ProfileDir("chrome-copied-profile"); // a profile folder copied out of its user-data-dir
        Directory.CreateDirectory(copied);
        File.WriteAllText(Path.Combine(copied, "Preferences"), "{}");

        Assert.Equal(BrowserProfileFolderKind.BrowserUserData, BrowserProfileFiles.ClassifyFolder(defaultProfile));
        Assert.Equal(BrowserProfileFolderKind.BrowserUserData, BrowserProfileFiles.ClassifyFolder(secondProfile));
        Assert.Equal(BrowserProfileFolderKind.BrowserUserData, BrowserProfileFiles.ClassifyFolder(bareProfile));
        Assert.Equal(BrowserProfileFolderKind.BrowserUserData, BrowserProfileFiles.ClassifyFolder(copied));
    }

    [Fact]
    public void ARealFirefoxProfileIsRefused()
    {
        var dir = ProfileDir("firefox-everyday");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "prefs.js"), "// Mozilla User Preferences\n");
        File.WriteAllText(Path.Combine(dir, "places.sqlite"), "");

        Assert.Equal(BrowserProfileFolderKind.BrowserUserData, BrowserProfileFiles.ClassifyFolder(dir));
    }

    [Fact]
    public void AProfileThisApplicationCreatedIsAccepted()
    {
        var chrome = ProfileDir("ours-chrome");
        BrowserProfileFiles.Create(BrowserKind.Chrome, chrome, "http://", "127.0.0.1", 18081, "localhost");
        Assert.True(File.Exists(Path.Combine(chrome, BrowserProfileFiles.MarkerFileName)));
        Assert.Equal(BrowserProfileFolderKind.Ours, BrowserProfileFiles.ClassifyFolder(chrome)); // no config needed

        var firefox = ProfileDir("ours-firefox");
        BrowserProfileFiles.Create(BrowserKind.Firefox, firefox, "socks5", "127.0.0.1", 18083, "localhost");
        Assert.Equal(BrowserProfileFolderKind.Ours, BrowserProfileFiles.ClassifyFolder(firefox));
    }

    [Fact]
    public void AOlderFirefoxProfileOfOursIsRecognisedByItsManagedUserJs()
    {
        var dir = ProfileDir("old-firefox");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, BrowserProfileFiles.FirefoxUserJsName),
            BrowserProfileFiles.RenderFirefoxUserJs("socks5", "127.0.0.1", 18083, "localhost"));
        File.WriteAllText(Path.Combine(dir, "prefs.js"), "// Mozilla User Preferences\n");

        Assert.Equal(BrowserProfileFolderKind.Ours, BrowserProfileFiles.ClassifyFolder(dir));
    }

    [Fact]
    public void AnEmptyOrMissingFolderIsFreshAndAForeignOneIsOnlyAnAdvisory()
    {
        var empty = ProfileDir("empty-folder");
        Directory.CreateDirectory(empty);
        Assert.Equal(BrowserProfileFolderKind.Fresh, BrowserProfileFiles.ClassifyFolder(empty));
        Assert.Equal(BrowserProfileFolderKind.Fresh, BrowserProfileFiles.ClassifyFolder(ProfileDir("missing-folder")));

        var foreign = ProfileDir("foreign-folder");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "notes.txt"), "x");
        Assert.Equal(BrowserProfileFolderKind.Foreign, BrowserProfileFiles.ClassifyFolder(foreign));
        // the configured path is ours when nothing marks it as a browser's own data
        Assert.Equal(BrowserProfileFolderKind.Ours, BrowserProfileFiles.ClassifyFolder(foreign, foreign));
    }

    // ----- the executable lookup -----

    [Fact]
    public void LocatorPrefersTheFirstExistingStandardPlace()
    {
        var candidates = BrowserExecutableLocator.Candidates(BrowserKind.Chrome);
        Assert.True(candidates.Count >= 3);
        Assert.All(candidates, path => Assert.DoesNotContain('%', path)); // the variables are expanded already

        Assert.Equal(
            candidates[1],
            BrowserExecutableLocator.Find(BrowserKind.Chrome, path => path == candidates[1]));
        Assert.Null(BrowserExecutableLocator.Find(BrowserKind.Firefox, _ => false));
    }

    // ----- the service -----

    [Fact]
    public void CreateWritesTheProfileTheShortcutAndTheConfig()
    {
        var config = NewConfig();
        var dir = ProfileDir();
        var service = CreateService(config, path => path == @"C:\fake\firefox.exe");

        var result = service.Create(new BrowserProfileRequest(
            BrowserKind.Firefox,
            dir,
            @"C:\fake\firefox.exe",
            CreateDesktopShortcut: true));

        Assert.Equal(dir, result.ProfileDir);
        Assert.Contains(@"user_pref(""network.proxy.socks_port"", 18083);", File.ReadAllText(Path.Combine(dir, "user.js")));

        // the shortcut goes to the desktop with the egress baked in
        var shortcut = Assert.Single(_shortcuts.Created);
        Assert.Equal(Path.Combine(_desktop, "PrivateNetRouting Firefox.lnk"), shortcut.ShortcutPath);
        Assert.Equal(@"C:\fake\firefox.exe", shortcut.TargetPath);
        Assert.Equal($"-profile {dir} --new-instance", shortcut.Arguments);
        Assert.Contains("Firefox", shortcut.Description);

        // and the config knows the profile at once
        Assert.True(config.Browser.IsConfigured);
        Assert.Equal(BrowserKind.Firefox, config.Browser.Kind);
        Assert.Equal(dir, config.Browser.ProfileDir);
        Assert.Equal(shortcut.ShortcutPath, config.Browser.ShortcutPath);
        Assert.Single(_persisted); // the user config file got the same values
    }

    [Fact]
    public void CreateFollowsTheForwardProxyEgressByDefault()
    {
        var config = NewConfig();
        config.Browser.Egress = "forward-proxy";
        var dir = ProfileDir("forward-proxy");
        var service = CreateService(config, path => path == @"C:\fake\firefox.exe");

        service.Create(new BrowserProfileRequest(
            BrowserKind.Firefox,
            dir,
            @"C:\fake\firefox.exe",
            CreateDesktopShortcut: false));

        // the default egress is the application's CONNECT proxy: http/ssl prefs, not SOCKS5
        var js = File.ReadAllText(Path.Combine(dir, "user.js"));
        Assert.Contains(@"user_pref(""network.proxy.http_port"", 18081);", js);
        Assert.DoesNotContain(@"user_pref(""network.proxy.socks_port"", 18083);", js);
    }

    [Fact]
    public void TheShortcutCarriesAnIpv6EgressBracketed()
    {
        var config = NewConfig();
        config.Browser.Egress = "forward-proxy";
        config.ForwardProxy.Listen = "[::1]:18081";
        var service = CreateService(config, path => path == @"C:\fake\chrome.exe");

        service.Create(new BrowserProfileRequest(
            BrowserKind.Chrome,
            ProfileDir("ipv6"),
            @"C:\fake\chrome.exe",
            CreateDesktopShortcut: true));

        // "http://::1:18081" is not a URI: the shortcut needs the same bracketed form the button uses
        var shortcut = Assert.Single(_shortcuts.Created);
        Assert.Contains("--proxy-server=http://[::1]:18081", shortcut.Arguments);
    }

    [Fact]
    public void CreateWithoutTheShortcutLeavesTheDesktopAloneAndClearsThePath()
    {
        var config = NewConfig();
        var service = CreateService(config, path => path == @"C:\fake\chrome.exe");

        var result = service.Create(new BrowserProfileRequest(
            BrowserKind.Chrome,
            ProfileDir("no-shortcut"),
            @"C:\fake\chrome.exe",
            CreateDesktopShortcut: false));

        Assert.Equal("", result.ShortcutPath);
        Assert.Empty(_shortcuts.Created);
        Assert.Equal("", config.Browser.ShortcutPath);
        Assert.Equal(BrowserKind.Chrome, config.Browser.Kind);
        Assert.True(File.Exists(Path.Combine(result.ProfileDir, BrowserProfileFiles.ChromiumPreferencesRelativePath)));
    }

    [Fact]
    public void CreateHonorsACustomShortcutPathAndCreatesItsFolder()
    {
        var config = NewConfig();
        var service = CreateService(config, path => path == @"C:\fake\firefox.exe");
        var shortcutPath = Path.Combine(_root, "launchers", "ff-proxy.lnk"); // the folder does not exist yet

        var result = service.Create(new BrowserProfileRequest(
            BrowserKind.Firefox,
            ProfileDir("custom-shortcut"),
            @"C:\fake\firefox.exe",
            CreateDesktopShortcut: true,
            ShortcutPath: shortcutPath));

        var shortcut = Assert.Single(_shortcuts.Created);
        Assert.Equal(shortcutPath, shortcut.ShortcutPath);
        Assert.True(Directory.Exists(Path.GetDirectoryName(shortcutPath)));
        Assert.Equal(shortcutPath, result.ShortcutPath);
        Assert.Equal(shortcutPath, config.Browser.ShortcutPath);
    }

    [Fact]
    public void CreateRefusesAMissingExecutable()
    {
        var config = NewConfig();
        var service = CreateService(config, _ => false);

        Assert.Throws<FileNotFoundException>(() => service.Create(new BrowserProfileRequest(
            BrowserKind.Firefox,
            ProfileDir("missing"),
            @"C:\gone\firefox.exe",
            CreateDesktopShortcut: true)));

        Assert.False(config.Browser.IsConfigured);
        Assert.Empty(_shortcuts.Created);
    }

    [Fact]
    public void CreateRefusesAnEmptyExecutable()
    {
        var service = CreateService(NewConfig(), _ => true);

        var error = Assert.Throws<FileNotFoundException>(() => service.Create(new BrowserProfileRequest(
            BrowserKind.Firefox,
            ProfileDir("empty"),
            "  ",
            CreateDesktopShortcut: false)));

        Assert.Contains("not specified", error.Message);
    }

    [Fact]
    public void SuggestProfileDirKeepsTheFamiliesApart()
    {
        var firefox = BrowserProfileService.SuggestProfileDir(BrowserKind.Firefox);
        var chrome = BrowserProfileService.SuggestProfileDir(BrowserKind.Chrome);

        Assert.NotEqual(firefox, chrome);
        Assert.Contains("firefox", firefox);
        Assert.Contains("chrome", chrome);
    }

    private sealed class FakeShortcuts : IShortcutFactory
    {
        public List<(string ShortcutPath, string TargetPath, string Arguments, string WorkingDirectory, string Description)> Created { get; } = [];

        public void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string description) =>
            Created.Add((shortcutPath, targetPath, arguments, workingDirectory, description));
    }

    public void Dispose()
    {
        _log.Dispose();
        foreach (var path in new[] { _logPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (var dir in new[] { _root, _desktop })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
