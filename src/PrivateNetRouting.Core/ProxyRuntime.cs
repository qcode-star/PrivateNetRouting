using PrivateNetRouting.Core.AppUpdate;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Runtime;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Updater;
using PrivateNetRouting.Core.Watchdog;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace PrivateNetRouting.Core;

/// <summary>
/// Composition of the forward proxy and the chain from the config — the single assembly point for the
/// WPF application and the CLI. Owns the log, the DoH pool and the proxy itself; Dispose stops everything.
/// </summary>
public sealed class ProxyRuntime : IAsyncDisposable
{
    public AppConfig Config { get; }

    /// <summary>
    /// The proxy server itself (in-process); the console mode and the worker start it directly.
    /// Null in the skipped shapes (the http mode, or socks5 with forwardProxy.enabled=false):
    /// there is no local proxy.
    /// </summary>
    public ForwardProxyService? ProxyService { get; }

    /// <summary>Port listener management: in-process or a detached worker (forwardProxy.detached).</summary>
    public ForwardProxySupervisor ProxyControl { get; }

    public FileLog Log { get; }

    /// <summary>The DoH pool — the resolver set of the tailscale mode; null in the external-upstream modes.</summary>
    public DohEndpointPool? DohPool { get; }

    public ChainSupervisor Chain { get; }

    public ClaudeLauncher Launcher { get; }

    /// <summary>Codex desktop app launcher (Microsoft Store MSIX, package-context launch).</summary>
    public CodexLauncher CodexLauncher { get; }

    public BrowserLauncher Browser { get; }

    /// <summary>Creation of the browser profile (the "?" help menu dialog): the profile files, the desktop shortcut and the write-back into the user config.</summary>
    public BrowserProfileService BrowserProfile { get; }

    public WatchdogService Watchdog { get; }

    /// <summary>Idle stopper lifecycle from the outside: the detached worker start/stop, liveness by command lines.</summary>
    public WatchdogSupervisor WatchdogControl { get; }

    public ClaudeUpdater Updater { get; }

    /// <summary>Codex desktop app update through the Microsoft Store.</summary>
    public CodexUpdater CodexUpdater { get; }

    public GuardService Guard { get; }

    public DiagnosticsService Diagnostics { get; }

    public LogStore Logs { get; }

    /// <summary>App self-update: the running version against the repository's GitHub releases
    /// (the help-menu item and the one-time start check).</summary>
    public AppUpdateChecker AppUpdate { get; }

    private readonly FileLog _chainLog;

    private readonly FileLog _launcherLog;

    private readonly FileLog _codexLauncherLog;

    private readonly FileLog _browserLog;

    private readonly FileLog _watchdogLog;

    private readonly FileLog _updaterLog;

    private readonly FileLog _codexUpdaterLog;

    private readonly FileLog _guardLog;

    private readonly HttpClient _updaterHttp;

    private readonly FileLog _appUpdateLog;

    private readonly List<FileLog> _allLogs;

    private ProxyRuntime(AppConfig config, FileLog log, DohEndpointPool? pool, ForwardProxyService? proxy, ForwardProxySupervisor proxyControl, ChainSupervisor chain, FileLog chainLog, ClaudeLauncher launcher, FileLog launcherLog, CodexLauncher codexLauncher, FileLog codexLauncherLog, BrowserLauncher browser, BrowserProfileService browserProfile, FileLog browserLog, WatchdogService watchdog, FileLog watchdogLog, WatchdogSupervisor watchdogControl, ClaudeUpdater updater, FileLog updaterLog, CodexUpdater codexUpdater, FileLog codexUpdaterLog, HttpClient updaterHttp, GuardService guard, FileLog guardLog, DiagnosticsService diagnostics, LogStore logs, AppUpdateChecker appUpdate, FileLog appUpdateLog, List<FileLog> allLogs)
    {
        Config = config;
        Log = log;
        DohPool = pool;
        ProxyService = proxy;
        ProxyControl = proxyControl;
        Chain = chain;
        _chainLog = chainLog;
        Launcher = launcher;
        _launcherLog = launcherLog;
        CodexLauncher = codexLauncher;
        _codexLauncherLog = codexLauncherLog;
        Browser = browser;
        _browserLog = browserLog;
        BrowserProfile = browserProfile;
        Watchdog = watchdog;
        _watchdogLog = watchdogLog;
        WatchdogControl = watchdogControl;
        Updater = updater;
        _updaterLog = updaterLog;
        CodexUpdater = codexUpdater;
        _codexUpdaterLog = codexUpdaterLog;
        _updaterHttp = updaterHttp;
        Guard = guard;
        _guardLog = guardLog;
        Diagnostics = diagnostics;
        Logs = logs;
        AppUpdate = appUpdate;
        _appUpdateLog = appUpdateLog;
        _allLogs = allLogs;
    }

    public static ProxyRuntime FromConfig(AppConfig config, string logFileName = "forward-proxy.log")
    {
        var fp = config.ForwardProxy;
        FileLog NewLog(string fileName) =>
            new(Path.Combine(config.Logging.Directory, fileName), config.Logging.EffectiveLevel, config.Logging.MaxBytes, config.Logging.Keep);
        var log = NewLog(logFileName);

        var mode = config.Chain.EffectiveMode;
        // the external-upstream modes (socks5, http): the trusted upstream resolves the names
        // carried by the CONNECT — nothing in this process resolves anything
        var external = mode is ChainMode.Socks5 or ChainMode.Http;
        var (upstream, upstreamUser, upstreamPassword) = ResolveUpstream(config);
        var socks = new Socks5Client(upstream, upstreamUser, upstreamPassword);

        // The DoH machinery is a tailscale-mode tool: the sidecar's own resolution is poisoned by
        // the bans, so names resolve through the tailnet DoH (discovered from the netmap or taken
        // from the seed set). In the external-upstream modes the trusted upstream resolves the
        // name carried by the CONNECT — no pool is built and no probes run.
        DohEndpointPool? pool = null;
        CachingDnsResolver? resolver = null;
        if (external)
        {
            log.Info(mode == ChainMode.Http
                ? "doh: not used in the http mode: names go to the HTTP upstream with the CONNECT"
                : "doh: not used in the socks5 mode: names go to the upstream with the CONNECT");
        }
        else
        {
            var seed = fp.DohSeed
                .Select(s => DohEndpoint.TryParse(s, out var ep) && ep is not null ? ep : null)
                .OfType<DohEndpoint>()
                .ToList();
            var rejected = fp.DohSeed.Count - seed.Count;
            if (rejected > 0)
            {
                log.Warn($"doh: skipped malformed seed endpoints: {rejected}");
            }

            // netmap discovery reads the tailnet config through the sidecar pipe; disabled — the
            // pool lives on the seed set alone
            IDohEndpointSource? source = fp.DohDiscovery.Enabled
                ? new TailscaleNetmapDohSource(config.Chain.Tailscale, log)
                : null;
            if (source is null && seed.Count == 0)
            {
                log.Warn("doh: discovery is off and the seed set is empty — name resolution will not work");
            }
            pool = new DohEndpointPool(
                socks,
                seed,
                log,
                TimeSpan.FromSeconds(fp.DohRefreshSeconds),
                TimeSpan.FromSeconds(fp.DohProbeTimeoutSeconds),
                TimeSpan.FromSeconds(fp.DohDeadCooldownSeconds),
                fp.DohProbeName,
                source);

            resolver = new CachingDnsResolver(
                new DohResolver(socks, pool, TimeSpan.FromSeconds(fp.DohTimeoutSeconds)),
                TimeSpan.FromSeconds(fp.CacheTtlMinSeconds),
                TimeSpan.FromSeconds(fp.CacheTtlMaxSeconds));
        }

        var marker = new ActivityMarker(
            fp.ActivityMarkerPath,
            TimeSpan.FromSeconds(fp.ActivityTouchIntervalSeconds));

        // the skipped shapes (the http mode, or socks5 with forwardProxy.enabled=false): no local
        // proxy at all — the applications point at the external upstream itself, the supervisor is
        // a no-op
        var skipProxy = AppProxyEndpoint.IsSkipped(config);
        if (skipProxy)
        {
            log.Info(mode == ChainMode.Http
                ? $"forward-proxy: the http mode runs no local proxy — the applications point at {config.Chain.Http.Upstream}"
                : $"forward-proxy: skipped in the socks5 mode (forwardProxy.enabled=false) — the applications point at {config.Chain.Socks5.Upstream}");
            if (mode == ChainMode.Http && config.ForwardProxy.Enabled)
            {
                log.Warn("forward-proxy: forwardProxy.enabled=true is ignored in the http mode — the provided HTTP upstream is the applications endpoint");
            }
        }

        ForwardProxyService? proxy = skipProxy
            ? null
            : new ForwardProxyService(fp, resolver, socks, marker, log, passHostNameToUpstream: mode == ChainMode.Socks5);

        var ports = new TcpPortProbe();
        var processes = new WmiProcessFinder();
        var tcpTable = new TcpConnectionTable();
        var proxyControl = new ForwardProxySupervisor(fp, proxy, ports, tcpTable, processes, log,
            skipped: skipProxy,
            skippedAppsEndpoint: skipProxy ? AppProxyEndpoint.Address(config) : null,
            skippedReason: skipProxy ? SkipReason(config) : null);
        var watchdogLog = NewLog("watchdog.log");
        var watchdogControl = new WatchdogSupervisor(config.Watchdog, processes, watchdogLog);
        var chainLog = NewLog("chain.log");
        var elevated = new ElevatedRunner(config.Chain.Tailscale, chainLog);
        var chain = new ChainSupervisor(
            config.Chain,
            proxyControl,
            new SidecarDaemon(config.Chain.Tailscale, ports, processes, chainLog, elevated,
                new TailscaleVersionProbe(config.Chain.Tailscale.TailscaledPath)),
            new SidecarKeeper(config.Chain.Tailscale, processes, chainLog),
            new SidecarCli(config.Chain.Tailscale),
            chainLog,
            ct => ChainHealthAsync(config, ct),
            // after every successful raise the idle stopper re-arms itself (watchdog.autoStart);
            // fire and forget: the raise result must not wait for the worker spawn — the pending
            // task is tracked on the supervisor so a CLI process can await it before exiting
            watchdogControl.TrackAutoStart,
            // a completed stop takes the idle stopper down with the chain (awaited: "chain down"
            // means no stoppers either — the GUI and `pnr chain down` return to a clean state)
            ct => watchdogControl.StopAsync(ct),
            config.Chain.EffectiveMode is ChainMode.Socks5 or ChainMode.Http
                ? ct => EgressCheckAsync(config, socks, chainLog, ct)
                : null,
            // the first node login picks up the control server of the machine's system tailscale
            // when the config names none
            new SystemControlUrlFinder(config.Chain.Tailscale, chainLog).FindAsync,
            proxyDisabled: !config.ForwardProxy.Enabled);

        var launcherLog = NewLog("launcher.log");
        var launcher = new ClaudeLauncher(
            config,
            chain,
            ct => ChainHealthAsync(config, ct),
            processes,
            launcherLog);

        var codexLauncherLog = NewLog("codex-launcher.log");
        var codexLauncher = new CodexLauncher(
            config,
            chain,
            ct => ChainHealthAsync(config, ct),
            processes,
            codexLauncherLog,
            new PowerShellRunner(config.PowerShellPath));

        var browserLog = NewLog("browser.log");
        var browser = new BrowserLauncher(
            config,
            chain,
            browserLog,
            processes);
        var browserProfile = new BrowserProfileService(config, new WScriptShortcutFactory(), browserLog);

        IWslAppHost? wslHost = config.Wsl.Distros.Count == 0
            ? null
            : new WslAppHostStopper(
                config.Wsl.Distros.Select(distro => distro.Name).ToList(),
                config.Claude.Enabled,
                config.Codex.Enabled,
                new WslProcessRunner());
        // The idle stopper also reads the chain's egress listener: the browser profile goes straight
        // there (mitigating the application proxy), so its live connections are what keeps the chain
        // from being stopped under an open browser. In the external-upstream modes the egress is
        // external — stopping the chain does not cut the browser, and the port means nothing (0).
        // The proxy port is the applications proxy endpoint: the local listener's port in the
        // default shape, the external endpoint's in a skipped one (the clients connect there all
        // the same).
        var egressPort = mode == ChainMode.Tailscale
            ? Socks5Client.ParseEndPoint(config.Chain.Tailscale.SocksListen).Port
            : 0;
        var usage = new ClaudeUsageProbe(
            processes,
            new TcpEstablishedConnectionCounter(),
            AppProxyEndpoint.Port(config),
            skipProxy ? null : fp.ActivityMarkerPath,
            config.Codex.ProcessName,
            egressPort);
        var watchdog = new WatchdogService(config.Watchdog, usage, wslHost, chain, watchdogLog);

        var updaterLog = NewLog("updater.log");
        // the download goes through the applications proxy endpoint (the local CONNECT proxy, or
        // the external mixed inbound in the skipped shape); the global HttpClient timeout is
        // disabled — the limit is set per fragment (chunkTimeoutSeconds)
        var updaterHttp = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(AppProxyEndpoint.Url(config)),
            UseProxy = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var powershell = new PowerShellRunner(config.PowerShellPath);
        var updater = new ClaudeUpdater(
            config,
            chain,
            ct => ChainHealthAsync(config, ct),
            new PowerShellAppxPackageSource(powershell, updaterLog),
            new PowerShellJournalUrlFinder(config.Claude.Update, powershell, updaterLog),
            new UpdateFeedUrlFinder(updaterHttp, config.Claude.Update, updaterLog),
            new FragmentDownloader(updaterHttp, config.Claude.Update, updaterLog),
            marker,
            // the native Claude Code CLI is a claude.exe too: only the MSIX app's own processes
            // (by the install path) may be closed and killed for the update
            new RunningClaudeProcessSource(RunningApp.PackageInstallMarker(config.Claude.Update.PackageName)),
            updaterLog);

        // the Codex update goes through the Microsoft Store: the check and the install do not
        // touch the proxy chain at all (the Store CDN is not a banned domain); the offered
        // version is named by OpenAI's manifest under a banned host — it is asked only when the
        // chain is already alive, through the same proxied client the Claude feed uses
        var codexUpdaterLog = NewLog("codex-updater.log");
        var codexUpdater = new CodexUpdater(
            config,
            new PowerShellAppxPackageSource(powershell, codexUpdaterLog),
            new StoreUpdateSource(),
            // the OpenAI ChatGPT app shares the executable name: only the Codex package's own
            // processes (by the install path) may be closed and killed for the update
            new RunningClaudeProcessSource(RunningApp.PackageInstallMarker(config.Codex.PackageName)),
            new CodexVersionManifest(updaterHttp, config.Codex.Update, codexUpdaterLog),
            codexUpdaterLog);

        var guardLog = NewLog("guard.log");
        var guard = new GuardService(
            config,
            new RegistryEgressPolicyStore(),
            new PowerShellBanSystemOps(config, powershell),
            new ClaudeSettingsGuard(config),
            new CodexSettingsGuard(config),
            new WslConfigGuard(config, new WslProcessRunner()),
            new WinInetSystemProxyStore(),
            new ElevatedRunner(config.Chain.Tailscale, guardLog, executor: (command, resultFile) => GuardElevatedCommands.Execute(command, resultFile)),
            guardLog,
            ct => ChainHealthAsync(config, ct));

        var appUpdateLog = NewLog("appupdate.log");
        var appUpdate = new AppUpdateChecker(
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0),
            new GitHubAppReleaseFeed(),
            appUpdateLog);

        var logs = new LogStore(config.Logging.Directory);
        var allLogs = new List<FileLog> { log, chainLog, launcherLog, codexLauncherLog, browserLog, watchdogLog, updaterLog, codexUpdaterLog, guardLog, appUpdateLog };
        var diagnostics = new DiagnosticsService(
            config,
            chain,
            proxyControl,
            tcpTable,
            processes,
            ct => ChainProbeAsync(config, ct),
            guard.GetCoreStatusAsync,
            logs);

        return new ProxyRuntime(config, log, pool, proxy, proxyControl, chain, chainLog, launcher, launcherLog, codexLauncher, codexLauncherLog, browser, browserProfile, browserLog, watchdog, watchdogLog, watchdogControl, updater, updaterLog, codexUpdater, codexUpdaterLog, updaterHttp, guard, guardLog, diagnostics, logs, appUpdate, appUpdateLog, allLogs);
    }

    private static async Task<bool> ChainHealthAsync(AppConfig config, CancellationToken ct) =>
        (await ChainProbeAsync(config, ct)).Ok;

    /// <summary>
    /// The external-upstream egress check (socks5/http): a CONNECT-dial by name straight through
    /// the upstream — the same per-connection shape as the clients' own path (the name rides the
    /// tunnel, the upstream resolves on its side). socks5 speaks the SOCKS5 handshake; the http
    /// upstream answers HTTP CONNECT itself, so the roundtrip goes straight against it.
    /// </summary>
    private static async Task<bool> EgressCheckAsync(
        AppConfig config,
        Socks5Client socks,
        FileLog log,
        CancellationToken ct)
    {
        try
        {
            var fp = config.ForwardProxy;
            if (config.Chain.EffectiveMode == ChainMode.Http)
            {
                var endpoint = Socks5Client.ParseEndPoint(config.Chain.Http.Upstream);
                return await ProxyHealthCheck.CheckAsync(
                    endpoint.Address.ToString(),
                    endpoint.Port,
                    fp.HealthCheckHost,
                    fp.HealthCheckPort,
                    TimeSpan.FromSeconds(fp.SocksTimeoutSeconds),
                    ct);
            }

            using var connection = await socks.ConnectAsync(
                fp.HealthCheckHost,
                fp.HealthCheckPort,
                TimeSpan.FromSeconds(fp.SocksTimeoutSeconds),
                ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Debug($"chain: egress check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The skipped-shape log line: which mode skipped the local proxy and what stands in for it.</summary>
    private static string SkipReason(AppConfig config) =>
        config.Chain.EffectiveMode == ChainMode.Http
            ? $"forward-proxy: skipped in the http mode — the applications point at {config.Chain.Http.Upstream}"
            : $"forward-proxy: skipped in the socks5 mode (forwardProxy.enabled=false) — the applications point at {config.Chain.Socks5.Upstream}";

    private static Task<HealthProbeResult> ChainProbeAsync(AppConfig config, CancellationToken ct)
    {
        var endpoint = AppProxyEndpoint.EndPoint(config);
        return ProxyHealthCheck.ProbeAsync(
            endpoint.Address.ToString(),
            endpoint.Port,
            config.ForwardProxy.HealthCheckHost,
            config.ForwardProxy.HealthCheckPort,
            TimeSpan.FromSeconds(15),
            ct);
    }

    public Task StartAsync(CancellationToken ct = default) => ProxyControl.StartAsync(ct);

    public Task StopAsync() => ProxyControl.StopAsync();

    /// <summary>Re-reads the user config into the shared AppConfig object (the chain card "Reload" button).</summary>
    public void ReloadConfig() => ProxyConfigNeedsRestart = AppConfigStore.ReloadInto(Config);

    /// <summary>
    /// Whether the user file holds forwardProxy or chain values that differ from the ones this
    /// process runs with. ReloadConfig keeps those two sections as built (the proxy, the chain
    /// and every derived endpoint stay consistent), so the flag stays true until the file
    /// matches the running values again or the application restarts.
    /// </summary>
    public bool ProxyConfigNeedsRestart { get; private set; }

    /// <summary>The SOCKS5 upstream for the proxy's dials: in the socks5 mode — the external
    /// trusted proxy from chain.socks5 (with its RFC 1929 credentials); in the tailscale
    /// mode — the sidecar's own listener (chain.tailscale.socksListen), which needs no
    /// authentication. Deriving it from the daemon's listener keeps the address in one place:
    /// the proxy cannot drift from the listener it must dial. The http mode never builds the
    /// proxy (there is no dialer for an HTTP upstream and nothing needs one) — the value it
    /// gets here is never used.</summary>
    internal static (string EndPoint, string User, string Password) ResolveUpstream(AppConfig config) =>
        config.Chain.EffectiveMode == ChainMode.Socks5
            ? (config.Chain.Socks5.Upstream,
                config.Chain.Socks5.User,
                config.Chain.Socks5.Password)
            : (config.Chain.Tailscale.SocksListen, "", "");

    public Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var endpoint = AppProxyEndpoint.EndPoint(Config);
        return ProxyHealthCheck.CheckAsync(
            endpoint.Address.ToString(),
            endpoint.Port,
            Config.ForwardProxy.HealthCheckHost,
            Config.ForwardProxy.HealthCheckPort,
            timeout,
            ct);
    }

    /// <summary>
    /// Log rotation by file name: this process's live log is rotated in place
    /// (FileLog.RotateNow); a file nobody holds is rotated by shifting;
    /// one held by another process is reported as locked.
    /// </summary>
    public LogRotationResult RotateLog(string fileName)
    {
        var live = _allLogs.FirstOrDefault(l => Path.GetFileName(l.FilePath).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (live is not null)
        {
            if (!live.RotateNow())
            {
                return new LogRotationResult(fileName, false, "locked by another process");
            }

            live.Info("log: rotated manually");
            return LogRotationResult.Done(fileName);
        }

        return Logs.Rotate(fileName, Config.Logging.Keep);
    }

    public async ValueTask DisposeAsync()
    {
        // ConfigureAwait: Dispose is awaited blocking from the GUI (OnExit) — without it the
        // shutdown chain hits the captured Dispatcher context and deadlocks the process
        if (ProxyService is not null)
        {
            await ProxyService.DisposeAsync().ConfigureAwait(false);
        }

        DohPool?.Dispose();
        _chainLog.Dispose();
        _launcherLog.Dispose();
        _codexLauncherLog.Dispose();
        _browserLog.Dispose();
        _watchdogLog.Dispose();
        _updaterLog.Dispose();
        _codexUpdaterLog.Dispose();
        _guardLog.Dispose();
        _appUpdateLog.Dispose();
        _updaterHttp.Dispose();
        Log.Dispose();
    }
}
