using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>
/// Launch modes: normal, "check/bring up the chain only", and "prepare the profile
/// without opening the browser" (port of -PrepareOnly from browser-proxy.ps1). Url and the private window
/// override the config values (the CLI flag wins over browser.url/browser.private).
/// </summary>
public sealed record BrowserLauncherOptions(bool CheckOnly = false, bool PrepareOnly = false, bool Private = false, string? Url = null)
{
    public static BrowserLauncherOptions Default { get; } = new();
}

public enum BrowserLaunchOutcome
{
    /// <summary>The browser is started with the isolated profile and the application proxy.</summary>
    Launched,

    /// <summary>--check-only mode: the chain is alive, the browser was not started.</summary>
    CheckOnly,

    /// <summary>--prepare-only mode: the profile is prepared (Firefox — user.js), the browser was not started.</summary>
    Prepared,
}

public sealed record BrowserLaunchResult(BrowserLaunchOutcome Outcome, bool ChainRaised, TimeSpan Elapsed);

/// <summary>
/// Proxy bypass lists for the browser. Zone domains are shared with the Claude launcher
/// (launcher.bypassDomains); separately, top-level domains that go around the
/// proxy (*.ru and neighbors: local sites must not go out through the foreign egress).
/// </summary>
public static class BrowserBypassList
{
    /// <summary>
    /// Chromium-compatible list (--proxy-bypass-list, separator ";", CIDR supported):
    /// the base Claude list (localhost/private ranges/tailnet subnet) + domains in both
    /// forms + *.tld.
    /// </summary>
    public static string BuildChromium(IEnumerable<string> domains, IEnumerable<string> tlds)
    {
        var parts = new List<string> { ClaudeBypassList.Build(domains) };
        parts.AddRange(TrimTlds(tlds).Select(tld => "*." + tld));
        return string.Join(";", parts);
    }

    /// <summary>
    /// Firefox network.proxy.no_proxies_on (separator ",", CIDR NOT supported — names
    /// only): each zone domain in two forms — bare and with a leading dot (subdomains) + *.tld.
    /// </summary>
    public static string BuildFirefoxNoProxies(IEnumerable<string> domains, IEnumerable<string> tlds)
    {
        var parts = new List<string> { "localhost", "127.0.0.1", "::1", "*.local" };
        foreach (var domain in domains)
        {
            var zone = domain.Trim().TrimStart('.');
            if (zone.Length == 0)
            {
                continue;
            }

            parts.Add(zone);
            parts.Add("." + zone);
        }

        parts.AddRange(TrimTlds(tlds).Select(tld => "*." + tld));
        return string.Join(", ", parts);
    }

    private static IEnumerable<string> TrimTlds(IEnumerable<string> tlds) =>
        tlds
            .Select(tld => tld.Trim().TrimStart('.').TrimStart('*').TrimStart('.'))
            .Where(tld => tld.Length > 0);
}

/// <summary>user.js of the Firefox isolated profile (port of browser-proxy.ps1): rewritten on every launch.</summary>
public static class FirefoxUserJs
{
    public static string Render(string proxyHost, int proxyPort, string noProxies) => $"""
        // Managed by PrivateNetRouting — rewritten on every launch: proxy address and bypass list stay fresh.
        user_pref("network.proxy.type", 1);
        user_pref("network.proxy.http", "{proxyHost}");
        user_pref("network.proxy.http_port", {proxyPort});
        user_pref("network.proxy.ssl", "{proxyHost}");
        user_pref("network.proxy.ssl_port", {proxyPort});
        user_pref("network.proxy.share_proxy_settings", true);
        user_pref("network.proxy.failover_direct", false);
        user_pref("network.proxy.no_proxies_on", "{noProxies}");
        user_pref("network.trr.mode", 5);
        user_pref("browser.shell.checkDefaultBrowser", false);
        user_pref("datareporting.policy.dataSubmissionEnabled", false);
        """;
}

/// <summary>
/// Browser launcher with an isolated profile (port of browser-proxy.ps1): checks the chain with a live
/// CONNECT roundtrip, on failure brings it up idempotently (splash/progress — like the Claude
/// launcher), then starts the browser with the application proxy and without system settings: Firefox —
/// a separate profile with user.js and --new-instance (otherwise the URL goes to the everyday instance without
/// a proxy), chromium-like — --proxy-server/--user-data-dir (a separate directory is mandatory:
/// an already running instance ignores the command-line flags).
/// </summary>
public sealed class BrowserLauncher(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IProcessFinder processFinder,
    FileLog log,
    Func<string, bool>? fileExists = null,
    Func<ProcessStartInfo, Process?>? startProcess = null)
{
    /// <summary>Fallback if the configured path is not found: the second standard directory of the family (as in browser-proxy.ps1).</summary>
    private static readonly string FirefoxFallbackPath = @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe";

    private static readonly string ChromiumFallbackPath = @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe";

    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? (psi => Process.Start(psi));

    /// <summary>Browser launcher log (the splash screen offers to open it on failure).</summary>
    public FileLog Log => log;

    /// <summary>Proxy URL for --proxy-server (Chromium) and user.js (Firefox).</summary>
    public string ProxyUrl => "http://" + config.ForwardProxy.Listen;

    public string BuildChromiumBypassList() =>
        BrowserBypassList.BuildChromium(config.Launcher.BypassDomains, config.Browser.DirectTlds);

    public string BuildFirefoxNoProxies() =>
        BrowserBypassList.BuildFirefoxNoProxies(config.Launcher.BypassDomains, config.Browser.DirectTlds);

    public async Task<BrowserLaunchResult> LaunchAsync(BrowserLauncherOptions options, IProgress<ChainStep>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var chainRaised = false;
        if (await healthCheck(ct))
        {
            log.Info($"browser: proxy {config.ForwardProxy.Listen} responds");
        }
        else
        {
            log.Info("browser: proxy is not responding — bringing the chain up");
            await chain.EnsureRunningAsync(progress, ct);
            chainRaised = true;
        }

        if (options.CheckOnly)
        {
            log.Info("browser: check-only, browser is not started");
            return new BrowserLaunchResult(BrowserLaunchOutcome.CheckOnly, chainRaised, watch.Elapsed);
        }

        var kind = config.Browser.EffectiveType;
        var exe = ResolveExecutable(kind);
        var url = string.IsNullOrWhiteSpace(options.Url) ? config.Browser.Url : options.Url!;
        var privateWindow = options.Private || config.Browser.Private;

        if (kind == BrowserKind.Firefox)
        {
            // user.js is rewritten on every launch — the proxy URL and bypass list stay fresh
            WriteUserJs();
            if (options.PrepareOnly)
            {
                log.Info("browser: prepare-only, user.js written, browser is not started");
                return new BrowserLaunchResult(BrowserLaunchOutcome.Prepared, chainRaised, watch.Elapsed);
            }

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            };
            info.ArgumentList.Add("-profile");
            info.ArgumentList.Add(config.Browser.Firefox.ProfileDir);
            info.ArgumentList.Add("--new-instance");
            if (privateWindow)
            {
                info.ArgumentList.Add("-private-window");
            }

            info.ArgumentList.Add(url);
            Start(info, exe);
            log.Info($"browser: firefox started with -profile/--new-instance (user.js proxy {config.ForwardProxy.Listen})");
        }
        else
        {
            if (options.PrepareOnly)
            {
                log.Info("browser: prepare-only, chromium needs no profile preparation");
                return new BrowserLaunchResult(BrowserLaunchOutcome.Prepared, chainRaised, watch.Elapsed);
            }

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            };
            info.ArgumentList.Add($"--proxy-server={ProxyUrl}");
            info.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList()}");
            info.ArgumentList.Add($"--user-data-dir={config.Browser.Chromium.ProfileDir}");
            info.ArgumentList.Add("--no-first-run");
            info.ArgumentList.Add("--no-default-browser-check");
            if (privateWindow)
            {
                info.ArgumentList.Add("--incognito");
            }

            info.ArgumentList.Add(url);
            Start(info, exe);
            log.Info("browser: chromium started with --proxy-server/--user-data-dir");
        }

        return new BrowserLaunchResult(BrowserLaunchOutcome.Launched, chainRaised, watch.Elapsed);
    }

    /// <summary>
    /// Whether our browser instance is already running (a process with our profile in the command line).
    /// Launching Firefox again on the same profile with --new-instance hits the profile lock —
    /// instead, the application window is shown (like the Claude shortcut mode).
    /// </summary>
    public bool IsOurBrowserRunning()
    {
        var kind = config.Browser.EffectiveType;
        var exeName = Path.GetFileNameWithoutExtension(
            kind == BrowserKind.Firefox ? config.Browser.Firefox.Path : config.Browser.Chromium.Path);
        var profileDir = kind == BrowserKind.Firefox ? config.Browser.Firefox.ProfileDir : config.Browser.Chromium.ProfileDir;
        try
        {
            return processFinder
                .FindByNameContains(exeName)
                .Any(p => p.CommandLine.Contains(profileDir, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            log.Warn($"browser: process scan failed: {ex.Message}");
            return false;
        }
    }

    private string ResolveExecutable(BrowserKind kind)
    {
        var primary = kind == BrowserKind.Firefox ? config.Browser.Firefox.Path : config.Browser.Chromium.Path;
        var fallback = Environment.ExpandEnvironmentVariables(
            kind == BrowserKind.Firefox ? FirefoxFallbackPath : ChromiumFallbackPath);
        if (_fileExists(primary))
        {
            return primary;
        }

        if (_fileExists(fallback))
        {
            log.Info($"browser: configured executable not found, using fallback {fallback}");
            return fallback;
        }

        throw new IOException($"browser executable not found ({primary}; fallback {fallback})");
    }

    private void WriteUserJs()
    {
        var endpoint = Socks5Client.ParseEndPoint(config.ForwardProxy.Listen);
        var dir = config.Browser.Firefox.ProfileDir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "user.js"),
            FirefoxUserJs.Render(endpoint.Address.ToString(), endpoint.Port, BuildFirefoxNoProxies()));
        log.Info($"browser: user.js updated (proxy {config.ForwardProxy.Listen}, bypass list rewritten)");
    }

    private void Start(ProcessStartInfo info, string exe)
    {
        try
        {
            using var started = _startProcess(info);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            throw new IOException($"failed to start the browser ({exe}): {ex.Message}", ex);
        }
    }
}
