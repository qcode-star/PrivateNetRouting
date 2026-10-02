using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Runtime;
using PrivateNetRouting.Core.SystemOps;

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

    private readonly FileLog _chainLog;

    private readonly FileLog _launcherLog;

    private ProxyRuntime(AppConfig config, FileLog log, DohEndpointPool pool, ForwardProxyService proxy, ChainSupervisor chain, FileLog chainLog, ClaudeLauncher launcher, FileLog launcherLog)
    {
        Config = config;
        Log = log;
        DohPool = pool;
        Proxy = proxy;
        Chain = chain;
        _chainLog = chainLog;
        Launcher = launcher;
        _launcherLog = launcherLog;
    }

    public static ProxyRuntime FromConfig(AppConfig config, string logFileName = "forward-proxy.log")
    {
        var fp = config.ForwardProxy;
        var log = new FileLog(
            Path.Combine(config.Logging.Directory, logFileName),
            config.Logging.MaxBytes,
            config.Logging.Keep);

        var socks = new Socks5Client(fp.Socks5Upstream);

        var seed = fp.DohSeed
            .Select(s => DohEndpoint.TryParse(s, out var ep) && ep is not null ? ep : null)
            .OfType<DohEndpoint>()
            .ToList();
        var rejected = fp.DohSeed.Count - seed.Count;
        if (rejected > 0)
        {
            log.Warn($"doh: пропущено некорректных seed-эндпоинтов: {rejected}");
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
        var chainLog = new FileLog(
            Path.Combine(config.Logging.Directory, "chain.log"),
            config.Logging.MaxBytes,
            config.Logging.Keep);
        var elevated = new ElevatedRunner(config.Chain, chainLog);
        var chain = new ChainSupervisor(
            config.Chain,
            proxy,
            new SidecarDaemon(config.Chain, ports, processes, chainLog, elevated),
            new SidecarKeeper(config.Chain, processes, chainLog),
            new SidecarCli(config.Chain),
            chainLog,
            ct => ChainHealthAsync(config, ct));

        var launcherLog = new FileLog(
            Path.Combine(config.Logging.Directory, "launcher.log"),
            config.Logging.MaxBytes,
            config.Logging.Keep);
        var launcher = new ClaudeLauncher(
            config,
            chain,
            ct => ChainHealthAsync(config, ct),
            processes,
            launcherLog);

        return new ProxyRuntime(config, log, pool, proxy, chain, chainLog, launcher, launcherLog);
    }

    private static async Task<bool> ChainHealthAsync(AppConfig config, CancellationToken ct)
    {
        var endpoint = Socks5Client.ParseEndPoint(config.ForwardProxy.Listen);
        return await ProxyHealthCheck.CheckAsync(
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

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        DohPool.Dispose();
        _chainLog.Dispose();
        _launcherLog.Dispose();
        Log.Dispose();
    }
}
