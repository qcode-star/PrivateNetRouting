using System.Net;
using System.Text.Json.Nodes;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>
/// Where a browser profile's traffic goes — chosen by browser.egress. "forward-proxy" (default):
/// the applications proxy endpoint (AppProxyEndpoint — the local forward proxy forwardProxy.listen,
/// or, when the local proxy does not run — the http mode, or the socks5 mode with
/// forwardProxy.enabled=false — the external endpoint), spoken over HTTP CONNECT, so the names
/// ride the CONNECT and are resolved on the proxy side — the local ban cannot poison them; in
/// the default shape the proxy process (the GUI in the tray or the detached worker) has to be
/// running, in a skipped shape the external upstream does. "chain": the chain's final egress —
/// the sidecar's own SOCKS5 listener (chain.daemonSocksListen) in the tailscale mode, the
/// external upstream (chain.socks5.upstream as SOCKS5, chain.http.upstream as HTTP CONNECT) in
/// the external modes; the profile then works on its own (its desktop shortcut needs no
/// application process), but the tailscale sidecar resolves names with the system resolver, and
/// names blocked from direct access do not open through it.
/// </summary>
public static class BrowserEgress
{
    public const string HttpScheme = "http";
    public const string Socks5Scheme = "socks5";

    public static string Resolve(AppConfig config) =>
        config.Browser.EgressMode == BrowserEgressMode.Chain
            ? config.Chain.EffectiveMode switch
            {
                ChainMode.Socks5 => config.Chain.Socks5.Upstream,
                ChainMode.Http => config.Chain.Http.Upstream,
                _ => config.Chain.Tailscale.SocksListen,
            }
            : AppProxyEndpoint.Address(config);

    /// <summary>
    /// The URL scheme the profile must speak to the egress: HTTP CONNECT everywhere but the
    /// chain's own SOCKS5 — the sidecar's listener in the tailscale mode and the socks5 mode's
    /// upstream; the http mode's upstream is an HTTP proxy itself, so its chain egress is http.
    /// </summary>
    public static string Scheme(AppConfig config) =>
        config.Browser.EgressMode == BrowserEgressMode.Chain
            ? (config.Chain.EffectiveMode == ChainMode.Http ? HttpScheme : Socks5Scheme)
            : HttpScheme;

    /// <summary>The egress as a proxy URL ("scheme://host:port") for command lines and logs.</summary>
    public static string ProxyUrl(AppConfig config) => $"{Scheme(config)}://{Resolve(config)}";

    /// <summary>The egress endpoint; a malformed config value surfaces as a format error.</summary>
    public static IPEndPoint ResolveEndpoint(AppConfig config) => Socks5Client.ParseEndPoint(Resolve(config));
}

/// <summary>
/// Files of a browser profile, written at creation (and refreshed on an application launch):
/// what makes the profile go through the egress, keep quiet on start and come back with the
/// owner's tabs. Firefox reads user.js on every start and it wins over the UI (a proxy entered
/// there rolls back), plus a one-time seed of the Terms-of-Use acceptance into prefs.js (see
/// <see cref="FirefoxTermsOfUseVersion"/>); Chromium keeps no proxy in the profile at all
/// (the address rides --proxy-server), so only the WebRTC policy is seeded for it.
/// </summary>
public static class BrowserProfileFiles
{
    public const string FirefoxUserJsName = "user.js";

    /// <summary>Firefox user preferences written by the browser itself (the Terms-of-Use seed goes here, not into user.js).</summary>
    public const string FirefoxPrefsJsName = "prefs.js";

    /// <summary>Chromium seed file, relative to the profile directory (--user-data-dir).</summary>
    public const string ChromiumPreferencesRelativePath = @"Default\Preferences";

    /// <summary>The WebRTC policy of the Chromium family: no full WebRTC switch exists there, the policy
    /// keeps WebRTC from going around the proxy (the real address does not leak; a site that needs calls
    /// still works over the proxied path) — verified live: with the policy the browser gathers no ICE
    /// candidate at all, without it a host candidate exposes the local address.</summary>
    public const string ChromiumWebRtcPolicy = "disable_non_proxied_udp";

    /// <summary>
    /// Terms-of-Use version pre-accepted for a fresh profile: a value above any version Firefox
    /// ships, so the welcome/terms page does not come back after a version bump (and the switch
    /// cannot be exported into a policy — a personal profile is the owner's own decision).
    /// It goes into prefs.js and only when that file does not exist yet: user.js wins over
    /// prefs.js on every start, so a value pinned there would keep the page coming back forever
    /// once Firefox raises its version; in prefs.js the browser stays free to update the value.
    /// </summary>
    public const int FirefoxTermsOfUseVersion = 99;

    /// <summary>Creates (or refreshes) the profile files of the family. Idempotent; no other file of the directory is touched.</summary>
    public static void Create(BrowserKind kind, string profileDir, string scheme, string proxyHost, int proxyPort, string noProxies)
    {
        Directory.CreateDirectory(profileDir);
        if (kind == BrowserKind.Firefox)
        {
            File.WriteAllText(
                Path.Combine(profileDir, FirefoxUserJsName),
                RenderFirefoxUserJs(scheme, proxyHost, proxyPort, noProxies));
            EnsureFirefoxTermsOfUseAccepted(profileDir);
            return;
        }

        EnsureChromiumWebRtcPolicy(profileDir);
    }

    /// <summary>
    /// prefs.js seed for a profile that has never been started: the Terms-of-Use page is accepted
    /// up front, so the first launch opens the browser instead of the welcome screen. An existing
    /// prefs.js belongs to the browser and is never rewritten.
    /// </summary>
    private static void EnsureFirefoxTermsOfUseAccepted(string profileDir)
    {
        var path = Path.Combine(profileDir, FirefoxPrefsJsName);
        if (File.Exists(path))
        {
            return;
        }

        File.WriteAllText(
            path,
            $"""
            // Mozilla User Preferences

            // Written once by PrivateNetRouting at profile creation: the welcome/terms page of a
            // fresh profile is skipped. The browser owns this file afterwards and may update it.
            user_pref("termsofuse.acceptedVersion", {FirefoxTermsOfUseVersion});
            user_pref("termsofuse.acceptedDate", "{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            """);
    }

    /// <summary>
    /// user.js of a Firefox profile: all protocols through the egress with remote name resolution
    /// (the proxy of the scheme answers for the names itself), the session restored on start, no
    /// startup offers, WebRTC and the built-in VPN off (both would go around the proxy and reveal
    /// the real address).
    /// </summary>
    public static string RenderFirefoxUserJs(string scheme, string proxyHost, int proxyPort, string noProxies)
    {
        var proxyBlock = scheme == BrowserEgress.Socks5Scheme
            ? $"""
              // All protocols go through the egress SOCKS5; names are resolved on the egress side,
              // and no_proxies_on keeps the configured zones (and the local names) on the direct route.
              user_pref("network.proxy.type", 1);
              user_pref("network.proxy.socks", "{proxyHost}");
              user_pref("network.proxy.socks_port", {proxyPort});
              user_pref("network.proxy.socks_version", 5);
              user_pref("network.proxy.socks_remote_dns", true);
              user_pref("network.proxy.http", "");
              user_pref("network.proxy.http_port", 0);
              user_pref("network.proxy.ssl", "");
              user_pref("network.proxy.ssl_port", 0);
              user_pref("network.proxy.share_proxy_settings", true);
              """
            : $"""
              // All protocols go through the application's forward proxy (HTTP CONNECT): names ride
              // the CONNECT and are resolved on the proxy side, and no_proxies_on keeps the configured
              // zones (and the local names) on the direct route.
              user_pref("network.proxy.type", 1);
              user_pref("network.proxy.http", "{proxyHost}");
              user_pref("network.proxy.http_port", {proxyPort});
              user_pref("network.proxy.ssl", "{proxyHost}");
              user_pref("network.proxy.ssl_port", {proxyPort});
              user_pref("network.proxy.socks", "");
              user_pref("network.proxy.socks_port", 0);
              user_pref("network.proxy.share_proxy_settings", true);
              """;

        return $"""
            // Managed by PrivateNetRouting: the profile is created once with these settings.
            // user.js is read on every start and wins over the UI — a proxy entered there rolls back.

            {proxyBlock}
            user_pref("network.proxy.failover_direct", false);
            user_pref("network.proxy.no_proxies_on", "{noProxies}");
            user_pref("network.trr.mode", 5);

            // The tabs of the last session come back on start
            user_pref("browser.startup.page", 3);

            // No startup offers: welcome, upgrade dialog, what's-new panel, default-browser prompt
            user_pref("browser.aboutwelcome.enabled", false);
            user_pref("browser.startup.upgradeDialog.enabled", false);
            user_pref("browser.messaging-system.whatsNewPanel.enabled", false);
            user_pref("browser.shell.checkDefaultBrowser", false);
            user_pref("browser.startup.homepage_override.mstone", "ignore");
            user_pref("browser.discovery.enabled", false);
            user_pref("datareporting.policy.dataSubmissionEnabled", false);
            user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);

            // WebRTC off: it would go around the proxy and reveal the real address (calls do not work in this profile)
            user_pref("media.peerconnection.enabled", false);
            user_pref("media.navigator.enabled", false);

            // The built-in VPN (IP Protection) would route around the proxy as well
            user_pref("browser.ipProtection.enabled", false);
            """;
    }

    /// <summary>
    /// The WebRTC policy of a Chromium profile, merged into its Preferences: the policy is the
    /// lever this family has for WebRTC (the command-line switch is dropped by newer builds).
    /// The keys of an existing file survive — only the policy is written.
    /// </summary>
    private static void EnsureChromiumWebRtcPolicy(string profileDir)
    {
        var path = Path.Combine(profileDir, ChromiumPreferencesRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = new JsonObject();
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                // the browser rewrites the file itself; an unreadable one is replaced by the seed
                root = new JsonObject();
            }
        }

        var webrtc = root["webrtc"] as JsonObject ?? new JsonObject();
        webrtc["ip_handling_policy"] = ChromiumWebRtcPolicy;
        root["webrtc"] = webrtc;
        File.WriteAllText(path, root.ToJsonString());
    }
}

/// <summary>
/// The command line of a profile launch. One builder for the desktop shortcut and for the
/// application's button: the shortcut carries the same profile and the same proxy, so the owner
/// gets the same browser wherever they start it from.
/// </summary>
public static class BrowserProfileCommandLine
{
    public static IReadOnlyList<string> Build(
        BrowserKind kind,
        string profileDir,
        string proxyUrl,
        string chromiumBypassList,
        bool privateWindow = false,
        string? url = null)
    {
        var args = new List<string>();
        if (kind == BrowserKind.Firefox)
        {
            // the proxy lives in user.js; --new-instance keeps the launch out of an everyday instance
            args.Add("-profile");
            args.Add(profileDir);
            args.Add("--new-instance");
            if (privateWindow)
            {
                args.Add("-private-window");
            }
        }
        else
        {
            // WebRTC needs no argument here: the profile's webrtc.ip_handling_policy is this
            // family's lever (the --force-webrtc-ip-handling-policy switch is gone from current
            // builds — verified live on Chrome 155: with the policy alone ICE gathers no candidate
            // at all, while a profile without it exposes a host candidate)
            args.Add($"--proxy-server={proxyUrl}");
            args.Add($"--proxy-bypass-list={chromiumBypassList}");
            // a separate directory is mandatory: a running instance would ignore every flag here
            args.Add($"--user-data-dir={profileDir}");
            args.Add("--no-first-run");
            args.Add("--no-default-browser-check");
            // the session of the previous run comes back (the preference itself is protected by the browser)
            args.Add("--restore-last-session");
            if (privateWindow)
            {
                args.Add("--incognito");
            }
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            args.Add(url);
        }

        return args;
    }

    /// <summary>The arguments as one Windows command line (for a .lnk).</summary>
    public static string ToCommandLine(IEnumerable<string> args) =>
        string.Join(" ", args.Select(Quote));

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('\t') ? "\"" + value + "\"" : value;
}

/// <summary>
/// Lookup of the browser executable in the standard places of the family — what the creation
/// dialog finds before it asks the owner to point at a file by hand.
/// </summary>
public static class BrowserExecutableLocator
{
    private static readonly string[] FirefoxCandidates =
    [
        @"%ProgramFiles%\Mozilla Firefox\firefox.exe",
        @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe",
    ];

    private static readonly string[] ChromeCandidates =
    [
        @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
        @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
        @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe",
        // Edge ships with Windows and is Chromium-like too: the last resort of the family
        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
        @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
    ];

    public static IReadOnlyList<string> Candidates(BrowserKind kind) =>
        (kind == BrowserKind.Firefox ? FirefoxCandidates : ChromeCandidates)
            .Select(Environment.ExpandEnvironmentVariables)
            .ToList();

    /// <summary>The first existing candidate, or null when the family is not installed in a standard place.</summary>
    public static string? Find(BrowserKind kind, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        return Candidates(kind).FirstOrDefault(exists);
    }
}

/// <summary>What the creation dialog asks for.</summary>
public sealed record BrowserProfileRequest(
    BrowserKind Kind,
    string ProfileDir,
    string ExecutablePath,
    bool CreateDesktopShortcut);

public sealed record BrowserProfileResult(string ExecutablePath, string ProfileDir, string ShortcutPath);

/// <summary>
/// Creation of the browser profile: the profile files, the desktop shortcut and the write-back
/// into the user config (the running application sees the new profile at once). Everything is
/// taken from the config of the moment — the browser egress address (browser.egress), the bypass
/// zones. Re-creating a profile over the same directory is idempotent; a profile in another
/// directory is never touched or deleted, the owner is only switched to the new one.
/// </summary>
public sealed class BrowserProfileService
{
    private readonly AppConfig _config;
    private readonly IShortcutFactory _shortcuts;
    private readonly FileLog _log;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<Action<AppConfig>> _persist;
    private readonly Func<string> _desktopDirectory;

    public BrowserProfileService(
        AppConfig config,
        IShortcutFactory shortcuts,
        FileLog log,
        Func<string, bool>? fileExists = null,
        Action<Action<AppConfig>>? persist = null,
        Func<string>? desktopDirectory = null)
    {
        _config = config;
        _shortcuts = shortcuts;
        _log = log;
        _fileExists = fileExists ?? File.Exists;
        _persist = persist ?? (mutate => AppConfigStore.Update(mutate));
        _desktopDirectory = desktopDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    /// <summary>Suggested profile directory for the dialog: under the application's own local data, one folder per family.</summary>
    public static string SuggestProfileDir(BrowserKind kind) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrivateNetRouting",
        "browser",
        kind == BrowserKind.Firefox ? "firefox-profile" : "chrome-profile");

    /// <summary>Shortcut path of the profile inside the desktop directory.</summary>
    public string ShortcutPathFor(BrowserKind kind) => Path.Combine(
        _desktopDirectory(),
        $"PrivateNetRouting {BrowserKindParser.DisplayName(kind)}.lnk");

    /// <summary>The first existing standard-place candidate of the family; null — the owner has to point at the file.</summary>
    public string? FindExecutable(BrowserKind kind) => BrowserExecutableLocator.Find(kind, _fileExists);

    public BrowserProfileResult Create(BrowserProfileRequest request)
    {
        var executable = request.ExecutablePath.Trim();
        if (executable.Length == 0)
        {
            throw new FileNotFoundException($"browser executable is not specified for {BrowserKindParser.DisplayName(request.Kind)}");
        }

        if (!_fileExists(executable))
        {
            throw new FileNotFoundException($"browser executable not found: {executable}");
        }

        var profileDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.ProfileDir.Trim()));
        var endpoint = BrowserEgress.ResolveEndpoint(_config);
        var scheme = BrowserEgress.Scheme(_config);
        var host = endpoint.Address.ToString();
        var noProxies = BrowserBypassList.BuildFirefoxNoProxies(_config.BypassDomains, _config.DirectTlds);
        var chromiumBypass = BrowserBypassList.BuildChromium(_config.BypassDomains, _config.DirectTlds);
        BrowserProfileFiles.Create(request.Kind, profileDir, scheme, host, endpoint.Port, noProxies);
        _log.Info($"browser: profile of {BrowserKindParser.DisplayName(request.Kind)} created in {profileDir} (egress {scheme}://{host}:{endpoint.Port})");

        var shortcutPath = "";
        if (request.CreateDesktopShortcut)
        {
            shortcutPath = ShortcutPathFor(request.Kind);
            var arguments = BrowserProfileCommandLine.Build(request.Kind, profileDir, $"{scheme}://{host}:{endpoint.Port}", chromiumBypass);
            _shortcuts.CreateShortcut(
                shortcutPath,
                executable,
                BrowserProfileCommandLine.ToCommandLine(arguments),
                Path.GetDirectoryName(executable) ?? profileDir,
                $"PrivateNetRouting browser profile ({BrowserKindParser.DisplayName(request.Kind)})");
            _log.Info($"browser: desktop shortcut created ({shortcutPath})");
        }

        _config.Browser.Type = BrowserKindParser.ToConfigValue(request.Kind);
        _config.Browser.ExecutablePath = executable;
        _config.Browser.ProfileDir = profileDir;
        _config.Browser.ShortcutPath = shortcutPath;
        var type = _config.Browser.Type;
        _persist(file =>
        {
            file.Browser.Type = type;
            file.Browser.ExecutablePath = executable;
            file.Browser.ProfileDir = profileDir;
            file.Browser.ShortcutPath = shortcutPath;
        });
        _log.Info("browser: profile recorded in the user config");
        return new BrowserProfileResult(executable, profileDir, shortcutPath);
    }
}
