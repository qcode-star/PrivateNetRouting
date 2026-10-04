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

namespace PrivateNetRouting.Core;

/// <summary>
/// Композиция forward-прокси и цепочки из конфига — единая точка сборки для WPF-приложения
/// и CLI. Владеет логом, пулом DoH и самим прокси; Dispose останавливает всё.
/// </summary>
public sealed class ProxyRuntime : IAsyncDisposable
{
    public AppConfig Config { get; }

    public ForwardProxyService Proxy { get; }

    public FileLog Log { get; }

    public DohEndpointPool DohPool { get; }

    public ChainSupervisor Chain { get; }

    public ClaudeLauncher Launcher { get; }

    public BrowserLauncher Browser { get; }

    public WatchdogService Watchdog { get; }

    public ClaudeUpdater Updater { get; }

    public GuardService Guard { get; }

    public DiagnosticsService Diagnostics { get; }

    public LogStore Logs { get; }

    private readonly FileLog _chainLog;

    private readonly FileLog _launcherLog;

    private readonly FileLog _browserLog;

    private readonly FileLog _watchdogLog;

    private readonly FileLog _updaterLog;

    private readonly FileLog _guardLog;

    private readonly HttpClient _updaterHttp;

    private readonly List<FileLog> _allLogs;

    private ProxyRuntime(AppConfig config, FileLog log, DohEndpointPool pool, ForwardProxyService proxy, ChainSupervisor chain, FileLog chainLog, ClaudeLauncher launcher, FileLog launcherLog, BrowserLauncher browser, FileLog browserLog, WatchdogService watchdog, FileLog watchdogLog, ClaudeUpdater updater, FileLog updaterLog, HttpClient updaterHttp, GuardService guard, FileLog guardLog, DiagnosticsService diagnostics, LogStore logs, List<FileLog> allLogs)
    {
        Config = config;
        Log = log;
        DohPool = pool;
        Proxy = proxy;
        Chain = chain;
        _chainLog = chainLog;
        Launcher = launcher;
        _launcherLog = launcherLog;
        Browser = browser;
        _browserLog = browserLog;
        Watchdog = watchdog;
        _watchdogLog = watchdogLog;
        Updater = updater;
        _updaterLog = updaterLog;
        _updaterHttp = updaterHttp;
        Guard = guard;
        _guardLog = guardLog;
        Diagnostics = diagnostics;
        Logs = logs;
        _allLogs = allLogs;
    }

    public static ProxyRuntime FromConfig(AppConfig config, string logFileName = "forward-proxy.log")
    {
        var fp = config.ForwardProxy;
        FileLog NewLog(string fileName) =>
            new(Path.Combine(config.Logging.Directory, fileName), config.Logging.EffectiveLevel, config.Logging.MaxBytes, config.Logging.Keep);
        var log = NewLog(logFileName);

        var socks = new Socks5Client(fp.Socks5Upstream);

        var seed = fp.DohSeed
            .Select(s => DohEndpoint.TryParse(s, out var ep) && ep is not null ? ep : null)
            .OfType<DohEndpoint>()
            .ToList();
        var rejected = fp.DohSeed.Count - seed.Count;
        if (rejected > 0)
        {
            log.Warn($"doh: skipped malformed seed endpoints: {rejected}");
        }

        IDohEndpointSource? source = fp.DohDiscovery.Enabled
            ? new TailscaleNetmapDohSource(fp.DohDiscovery)
            : null;
        var pool = new DohEndpointPool(
            socks,
            seed,
            log,
            TimeSpan.FromSeconds(fp.DohRefreshSeconds),
            TimeSpan.FromSeconds(fp.DohProbeTimeoutSeconds),
            TimeSpan.FromSeconds(fp.DohDeadCooldownSeconds),
            fp.DohProbeName,
            source);

        var resolver = new CachingDnsResolver(
            new DohResolver(socks, pool, TimeSpan.FromSeconds(fp.DohTimeoutSeconds)),
            TimeSpan.FromSeconds(fp.CacheTtlMinSeconds),
            TimeSpan.FromSeconds(fp.CacheTtlMaxSeconds));
        var marker = new ActivityMarker(
            fp.ActivityMarkerPath,
            TimeSpan.FromSeconds(fp.ActivityTouchIntervalSeconds));

        var proxy = new ForwardProxyService(fp, resolver, socks, marker, log);

        var ports = new TcpPortProbe();
        var processes = new WmiProcessFinder();
        var chainLog = NewLog("chain.log");
        var elevated = new ElevatedRunner(config.Chain, chainLog);
        var chain = new ChainSupervisor(
            config.Chain,
            proxy,
            new SidecarDaemon(config.Chain, ports, processes, chainLog, elevated),
            new SidecarKeeper(config.Chain, processes, chainLog),
            new SidecarCli(config.Chain),
            chainLog,
            ct => ChainHealthAsync(config, ct));

        var launcherLog = NewLog("launcher.log");
        var launcher = new ClaudeLauncher(
            config,
            chain,
            ct => ChainHealthAsync(config, ct),
            processes,
            launcherLog);

        var browserLog = NewLog("browser.log");
        var browser = new BrowserLauncher(
            config,
            chain,
            ct => ChainHealthAsync(config, ct),
            processes,
            browserLog);

        var watchdogLog = NewLog("watchdog.log");
        IWslClaudeHost? wslHost = string.IsNullOrWhiteSpace(config.Watchdog.WslDistro)
            ? null
            : new WslClaudeHostStopper(config.Watchdog.WslDistro, new WslProcessRunner());
        var usage = new ClaudeUsageProbe(
            processes,
            new TcpEstablishedConnectionCounter(),
            Socks5Client.ParseEndPoint(fp.Listen).Port,
            fp.ActivityMarkerPath);
        var watchdog = new WatchdogService(config.Watchdog, usage, wslHost, chain, watchdogLog);

        var updaterLog = NewLog("updater.log");
        // скачивание идёт через собственный forward-прокси (DoH-резолв, дозвон SOCKS5);
        // общий таймаут HttpClient отключён — лимит задаётся на фрагмент (chunkTimeoutSeconds)
        var updaterHttp = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy("http://" + fp.Listen),
            UseProxy = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var powershell = new PowerShellRunner(config.Updater.PowerShellPath);
        var updater = new ClaudeUpdater(
            config,
            chain,
            ct => ChainHealthAsync(config, ct),
            new PowerShellAppxPackageSource(powershell, updaterLog),
            new PowerShellJournalUrlFinder(config.Updater, powershell, updaterLog),
            new FragmentDownloader(updaterHttp, config.Updater, updaterLog),
            marker,
            new RunningClaudeProcessSource(),
            updaterLog);

        var guardLog = NewLog("guard.log");
        var guard = new GuardService(
            config,
            new RegistryEgressPolicyStore(),
            new PowerShellBanSystemOps(config.Guard, powershell),
            new ClaudeSettingsGuard(config),
            new WslConfigGuard(config, new WslProcessRunner()),
            new WinInetSystemProxyStore(),
            new ElevatedRunner(config.Chain, guardLog, executor: (command, resultFile) => GuardElevatedCommands.Execute(command, resultFile)),
            guardLog,
            ct => ChainHealthAsync(config, ct));

        var logs = new LogStore(config.Logging.Directory);
        var allLogs = new List<FileLog> { log, chainLog, launcherLog, browserLog, watchdogLog, updaterLog, guardLog };
        var diagnostics = new DiagnosticsService(
            config,
            chain,
            proxy,
            new TcpConnectionTable(),
            processes,
            ct => ChainProbeAsync(config, ct),
            guard.GetCoreStatusAsync,
            logs);

        return new ProxyRuntime(config, log, pool, proxy, chain, chainLog, launcher, launcherLog, browser, browserLog, watchdog, watchdogLog, updater, updaterLog, updaterHttp, guard, guardLog, diagnostics, logs, allLogs);
    }

    private static async Task<bool> ChainHealthAsync(AppConfig config, CancellationToken ct) =>
        (await ChainProbeAsync(config, ct)).Ok;

    private static Task<HealthProbeResult> ChainProbeAsync(AppConfig config, CancellationToken ct)
    {
        var endpoint = Socks5Client.ParseEndPoint(config.ForwardProxy.Listen);
        return ProxyHealthCheck.ProbeAsync(
            endpoint.Address.ToString(),
            endpoint.Port,
            config.ForwardProxy.HealthCheckHost,
            config.ForwardProxy.HealthCheckPort,
            TimeSpan.FromSeconds(15),
            ct);
    }

    public Task StartAsync(CancellationToken ct = default) => Proxy.StartAsync(ct);

    public Task StopAsync() => Proxy.StopAsync();

    public Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var fp = Config.ForwardProxy;
        var endpoint = Socks5Client.ParseEndPoint(fp.Listen);
        return ProxyHealthCheck.CheckAsync(
            endpoint.Address.ToString(),
            endpoint.Port,
            fp.HealthCheckHost,
            fp.HealthCheckPort,
            timeout,
            ct);
    }

    /// <summary>
    /// Ротация лога по имени файла: живой лог этого процесса ротируется на месте
    /// (FileLog.RotateNow — работает всегда), файл, который никто не держит, — сдвигом;
    /// занятое другим процессом отмечается как locked.
    /// </summary>
    public LogRotationResult RotateLog(string fileName)
    {
        var live = _allLogs.FirstOrDefault(l => Path.GetFileName(l.FilePath).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (live is not null)
        {
            live.RotateNow();
            live.Info("log: rotated manually");
            return LogRotationResult.Done(fileName);
        }

        return Logs.Rotate(fileName, Config.Logging.Keep);
    }

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        DohPool.Dispose();
        _chainLog.Dispose();
        _launcherLog.Dispose();
        _browserLog.Dispose();
        _watchdogLog.Dispose();
        _updaterLog.Dispose();
        _guardLog.Dispose();
        _updaterHttp.Dispose();
        Log.Dispose();
    }
}
