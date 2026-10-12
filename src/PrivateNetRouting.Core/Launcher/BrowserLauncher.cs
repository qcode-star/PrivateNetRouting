using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>
/// Proxy bypass lists for the browser profile. Zone domains are shared with the Claude launcher
/// (launcher.bypassDomains); separately, top-level domains that go around the
/// egress (*.ru and neighbors: local sites must not go out through the foreign exit).
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

/// <summary>
/// Launch modes: normal, and "report the egress state only". A private window and a URL come only
/// from the caller (the CLI flags): the profile itself opens as it is — the tabs of the last
/// session are restored by the profile, there is no URL configured for it.
/// </summary>
public sealed record BrowserLauncherOptions(
    bool CheckOnly = false,
    bool Private = false,
    string? Url = null,

    /// <summary>
    /// Asked before a launch when the egress does not answer; true — the launch goes ahead.
    /// The wording and the offer belong to the caller's UI; in the tailscale mode the launcher
    /// raises the chain itself right after a confirmed answer (the egress is our chain's listener
    /// there). Null — a dead egress fails the launch without asking.
    /// </summary>
    Func<CancellationToken, Task<bool>>? ConfirmDeadEgress = null)
{
    public static BrowserLauncherOptions Default { get; } = new();
}

public enum BrowserLaunchOutcome
{
    /// <summary>The browser is started with the profile.</summary>
    Launched,

    /// <summary>--check-only mode: the egress state is reported, the browser was not started.</summary>
    CheckOnly,

    /// <summary>The owner refused the question about a dead egress: nothing was started.</summary>
    Cancelled,
}

public sealed record BrowserLaunchResult(BrowserLaunchOutcome Outcome, bool EgressResponded, TimeSpan Elapsed);

/// <summary>
/// Browser launcher for the profile created once (BrowserProfileService). There is no chain of its
/// own in the browser's path: the profile goes through the browser egress chosen by browser.egress
/// (BrowserEgress) — the application's forward proxy by default, the chain's final SOCKS5 as the
/// self-sufficient alternative. Before a launch the egress is checked with a TCP probe; the only
/// case the launcher touches the chain is a confirmed answer about a dead egress when the egress
/// is ours to raise (the forward proxy in both modes, the sidecar's listener in the tailscale one).
/// Firefox gets its user.js rewritten on every launch through the application — the egress address
/// and the bypass list stay fresh, and the desktop shortcut reads the same file;
/// Chromium takes everything from the command line and keeps no proxy in the profile.
/// </summary>
public sealed class BrowserLauncher(
    AppConfig config,
    IChainSupervisor chain,
    FileLog log,
    IProcessFinder processFinder,
    Func<ProcessStartInfo, Process?>? startProcess = null,
    Func<string, bool>? fileExists = null)
{
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? (psi => Process.Start(psi));
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    /// <summary>Browser launcher log.</summary>
    public FileLog Log => log;

    /// <summary>The profile is created: the family is known, the executable and the profile directory are set.</summary>
    public bool IsConfigured => config.Browser.IsConfigured;

    /// <summary>The egress the profile goes through (host:port).</summary>
    public string EgressEndpoint => BrowserEgress.Resolve(config);

    /// <summary>The egress as a proxy URL ("scheme://host:port") for command lines and logs.</summary>
    public string EgressUrl => BrowserEgress.ProxyUrl(config);

    /// <summary>
    /// True — a dead egress is fixed by raising the chain: the forward proxy is part of the chain
    /// bring-up in both modes, and the chain egress is ours everywhere but the external upstream
    /// of the socks5 mode. With the local proxy skipped (forwardProxy.skipInSocks5Mode) the
    /// "forward-proxy" egress is that same external endpoint — not ours to raise either.
    /// </summary>
    public bool ChainOwnsEgress =>
        config.Browser.EgressMode == BrowserEgressMode.Chain
            ? config.Chain.EffectiveMode != ChainMode.Socks5
            : !AppProxyEndpoint.IsSkipped(config);

    public string BuildChromiumBypassList() =>
        BrowserBypassList.BuildChromium(config.Launcher.BypassDomains, config.Browser.DirectTlds);

    public string BuildFirefoxNoProxies() =>
        BrowserBypassList.BuildFirefoxNoProxies(config.Launcher.BypassDomains, config.Browser.DirectTlds);

    /// <summary>
    /// Readiness of the egress: a plain TCP connect (a SOCKS5 handshake would add nothing to the
    /// verdict). The caller's cancellation propagates as OperationCanceledException; a timeout or a
    /// refused connection is a warning in the log and false.
    /// </summary>
    public async Task<bool> IsEgressReachableAsync(CancellationToken ct)
    {
        IPEndPoint endpoint;
        try
        {
            endpoint = BrowserEgress.ResolveEndpoint(config);
        }
        catch (FormatException ex)
        {
            log.Warn($"browser: the egress address is not usable: {ex.Message}");
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.Browser.ProbeTimeoutSeconds)));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"browser: egress {endpoint} is not responding ({ex.Message})");
            return false;
        }
    }

    /// <param name="progress">The chain bring-up steps — only the confirmed dead-egress path raises the chain.</param>
    public async Task<BrowserLaunchResult> LaunchAsync(
        BrowserLauncherOptions options,
        IProgress<ChainStep>? progress,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("the browser profile is not created yet");
        }

        var watch = Stopwatch.StartNew();
        WarnAboutChainEgressResolution();
        var reachable = await IsEgressReachableAsync(ct).ConfigureAwait(false);
        if (reachable)
        {
            log.Info($"browser: egress {EgressUrl} responds");
        }

        if (options.CheckOnly)
        {
            log.Info("browser: check-only, browser is not started");
            return new BrowserLaunchResult(BrowserLaunchOutcome.CheckOnly, reachable, watch.Elapsed);
        }

        if (!reachable)
        {
            if (options.ConfirmDeadEgress is null)
            {
                throw new IOException($"the browser egress {EgressEndpoint} is not responding");
            }

            if (!await options.ConfirmDeadEgress(ct).ConfigureAwait(false))
            {
                log.Info("browser: the dead egress was not confirmed — launch cancelled");
                return new BrowserLaunchResult(BrowserLaunchOutcome.Cancelled, false, watch.Elapsed);
            }

            if (ChainOwnsEgress)
            {
                log.Info("browser: raising the chain (confirmed by the owner)");
                await chain.EnsureRunningAsync(progress, ct).ConfigureAwait(false);
            }
            else
            {
                log.Info("browser: launching over the upstream that did not answer (confirmed by the owner)");
            }
        }

        LaunchBrowser(options);
        return new BrowserLaunchResult(BrowserLaunchOutcome.Launched, reachable, watch.Elapsed);
    }

    /// <summary>
    /// Whether our browser instance is already running (a process with our profile in the command line).
    /// Launching Firefox again on the same profile hits the profile lock — instead, the application
    /// window is shown (like the Claude shortcut mode).
    /// </summary>
    public bool IsOurBrowserRunning()
    {
        if (!IsConfigured)
        {
            return false;
        }

        var exeName = Path.GetFileNameWithoutExtension(config.Browser.ExecutablePath);
        var profileDir = config.Browser.ProfileDir;
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

    /// <summary>
    /// The chain's own SOCKS5 in the tailscale mode is the sidecar's listener: it resolves names
    /// with the system resolver (tsdial never consults the netstack DNS), and the direct-access ban
    /// blackholes exactly the AI names there — through this egress they do not open.
    /// </summary>
    private void WarnAboutChainEgressResolution()
    {
        if (config.Browser.EgressMode == BrowserEgressMode.Chain && config.Chain.EffectiveMode != ChainMode.Socks5)
        {
            log.Warn(
                "browser: the egress is the chain's own SOCKS5 — it resolves names via the system resolver, " +
                "so names blocked from direct access will not open through it (browser.egress=forward-proxy avoids that)");
        }
    }

    private void LaunchBrowser(BrowserLauncherOptions options)
    {
        var kind = config.Browser.Kind ?? throw new InvalidOperationException("the browser profile is not created yet");
        var profileDir = config.Browser.ProfileDir;
        var executable = ResolveExecutable(kind);
        if (kind == BrowserKind.Firefox)
        {
            // user.js is ours; refreshing it on every application launch keeps the egress address
            // and the bypass list of the file fresh for the button and the desktop shortcut alike
            var endpoint = BrowserEgress.ResolveEndpoint(config);
            BrowserProfileFiles.Create(
                kind,
                profileDir,
                BrowserEgress.Scheme(config),
                endpoint.Address.ToString(),
                endpoint.Port,
                BuildFirefoxNoProxies());
            log.Info($"browser: user.js refreshed (egress {EgressUrl}, bypass list rewritten)");
        }

        var args = BrowserProfileCommandLine.Build(
            kind,
            profileDir,
            EgressUrl,
            BuildChromiumBypassList(),
            options.Private,
            options.Url);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        Start(info, executable);
        log.Info(kind == BrowserKind.Firefox
            ? $"browser: firefox started with the profile {profileDir}"
            : $"browser: chrome started (--proxy-server={EgressUrl})");
    }

    private string ResolveExecutable(BrowserKind kind)
    {
        var configured = config.Browser.ExecutablePath;
        if (_fileExists(configured))
        {
            return configured;
        }

        var found = BrowserExecutableLocator.Find(kind, _fileExists);
        if (found is not null)
        {
            log.Info($"browser: the configured executable is not available, using {found}");
            return found;
        }

        throw new IOException(
            $"browser executable not found ({configured}; looked in: {string.Join(", ", BrowserExecutableLocator.Candidates(kind))})");
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
