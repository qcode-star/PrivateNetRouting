using System.Net;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Diagnostics;

/// <summary>Подсветка строки сводки: зелёный — норма, янтарный — внимание, красный — проблема.</summary>
public enum DiagMark
{
    Neutral,
    Good,
    Warn,
    Bad,
}

/// <summary>Строка сводки: техническое имя (английское) и готовое значение.</summary>
public sealed record DiagLine(string Name, string Value, DiagMark Mark = DiagMark.Neutral);

/// <summary>Секция сводки; Key — stable-идентификатор (chain/proxy/health/guards/watchdog), GUI локализует заголовок по нему.</summary>
public sealed record DiagSection(string Key, IReadOnlyList<DiagLine> Lines);

/// <summary>
/// Сводка состояния. ChainStopped — краевой случай прежнего proxy-status.ps1: ни демона,
/// ни слушателя forward-прокси нет вовсе — это норма, пока Claude не нужен (короткое
/// сообщение вместо полной картины).
/// </summary>
public sealed record DiagnosticsSummary(bool ChainStopped, IReadOnlyList<DiagSection> Sections);

/// <summary>
/// Диагностика (порт proxy-status.ps1), только чтение: процессы цепочки и владельцы
/// слушателей (PID по таблице TCP, как Get-NetTCPConnection), WSL-бинд прокси, счётчики,
/// возраст маркера активности, детальный CONNECT-roundtrip (время и первая строка ответа),
/// ядро гардов (политика + бан) и живость сторожа. Ничего не поднимает и не останавливает.
/// </summary>
public sealed class DiagnosticsService(
    AppConfig config,
    IChainSupervisor chain,
    IProxyStatistics proxy,
    ITcpConnectionTable tcpTable,
    IProcessFinder processes,
    Func<CancellationToken, Task<HealthProbeResult>> healthProbe,
    Func<CancellationToken, Task<(LocalBanStatus Ban, EgressPolicyStatus Policy)>> guardCore,
    LogStore logs,
    TimeProvider? time = null)
{
    private const string WatchdogProcessName = "pnr.exe"; // CLI-процесс команды `pnr watchdog`

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<DiagnosticsSummary> GetSummaryAsync(CancellationToken ct)
    {
        var chainConfig = config.Chain;
        var daemonHttpPort = Socks5Client.ParseEndPoint(chainConfig.DaemonHttpListen).Port;
        var daemonSocksPort = Socks5Client.ParseEndPoint(chainConfig.DaemonSocksListen).Port;
        var proxyPort = Socks5Client.ParseEndPoint(config.ForwardProxy.Listen).Port;

        var state = await chain.GetStateAsync(ct);
        var daemonListeners = tcpTable.GetListeners(daemonHttpPort);
        var proxyListeners = tcpTable.GetListeners(proxyPort);
        if (daemonListeners.Count == 0 && proxyListeners.Count == 0 && !proxy.IsRunning)
        {
            return new DiagnosticsSummary(ChainStopped: true, Sections: []);
        }

        var daemonSocksListeners = tcpTable.GetListeners(daemonSocksPort);
        var guard = await guardCore(ct);
        var sections = new List<DiagSection>
        {
            ChainSection(state, daemonListeners, daemonSocksListeners),
            ProxySection(proxy, proxyListeners, proxyPort),
            await HealthSectionAsync(proxy.IsRunning || proxyListeners.Count > 0, ct),
            GuardsSection(guard.Ban, guard.Policy),
            WatchdogSection(),
            LogsSection(),
        };
        return new DiagnosticsSummary(ChainStopped: false, Sections: sections);
    }

    private DiagSection ChainSection(
        ChainState state,
        IReadOnlyList<TcpListenerEntry> daemonHttp,
        IReadOnlyList<TcpListenerEntry> daemonSocks)
    {
        var lines = new List<DiagLine>
        {
            new(
                $"daemon ({Socks5Client.ParseEndPoint(config.Chain.DaemonHttpListen).Port} http, {Socks5Client.ParseEndPoint(config.Chain.DaemonSocksListen).Port} socks)",
                DescribeOwners(daemonHttp.Concat(daemonSocks)),
                daemonHttp.Count > 0 ? DiagMark.Good : DiagMark.Bad),
            KeeperLine(),
            new(
                "node",
                state.BackendState ?? "no response",
                state.BackendState == "Running" ? DiagMark.Good : DiagMark.Warn),
        };
        return new DiagSection("chain", lines);
    }

    /// <summary>Кипер watch-ipn опознаётся так же, как SidecarKeeper: tailscale CLI + наш пайп + watch-ipn в cmdline.</summary>
    private DiagLine KeeperLine()
    {
        var keepers = processes
            .Find("tailscale.exe", config.Chain.Socket)
            .Where(p => p.CommandLine.Contains("watch-ipn", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return keepers.Count > 0
            ? new DiagLine("keeper watch-ipn", $"pid {string.Join(", ", keepers.Select(k => k.Id))}", DiagMark.Good)
            : new DiagLine("keeper watch-ipn", "not found - the chain may fall (engine teardown)", DiagMark.Warn);
    }

    private DiagSection ProxySection(
        IProxyStatistics proxy,
        IReadOnlyList<TcpListenerEntry> proxyListeners,
        int proxyPort)
    {
        var self = $"pid {Environment.ProcessId}";
        var proxyLine = proxy.IsRunning
            ? new DiagLine("forward proxy", $"running ({self})", DiagMark.Good)
            : proxyListeners.Count > 0
                ? new DiagLine(
                    "forward proxy",
                    $"port {proxyPort} is held by {DescribeOwners(proxyListeners)} - another process of ours",
                    DiagMark.Neutral)
                : new DiagLine("forward proxy", "stopped", DiagMark.Warn);

        var wslBind = proxyListeners
            .Select(l => l.Address)
            .Where(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any))
            .ToList();
        var lines = new List<DiagLine>
        {
            proxyLine,
            new(
                "listeners",
                proxy.ListenerEndpoints.Count > 0 ? string.Join(", ", proxy.ListenerEndpoints) : "-"),
            new(
                "WSL bind",
                wslBind.Count > 0 ? string.Join(", ", wslBind) : "-",
                wslBind.Count > 0 ? DiagMark.Good : DiagMark.Neutral),
            new(
                "connections",
                $"active {proxy.ActiveConnections}, total {proxy.TotalConnections}, failed {proxy.FailedConnections}"),
            ActivityLine(),
        };
        return new DiagSection("proxy", lines);
    }

    /// <summary>Возраст маркера активности (mtime): насколько недавно был проксированный запрос.</summary>
    private DiagLine ActivityLine()
    {
        try
        {
            if (!File.Exists(config.ForwardProxy.ActivityMarkerPath))
            {
                return new DiagLine("activity", "no marker yet (no proxied requests)");
            }

            var age = _time.GetUtcNow() - File.GetLastWriteTimeUtc(config.ForwardProxy.ActivityMarkerPath);
            return new DiagLine("activity", $"{Math.Max(0, age.TotalSeconds):0} s ago");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DiagLine("activity", $"marker read failed: {ex.Message}");
        }
    }

    /// <summary>CONNECT-roundtrip всей цепочкой: время и первая строка ответа (порт health-check прежней схемы).</summary>
    private async Task<DiagSection> HealthSectionAsync(bool proxyListening, CancellationToken ct)
    {
        if (!proxyListening)
        {
            return new DiagSection("health", [new DiagLine("CONNECT-roundtrip", "skipped: proxy is not listening")]);
        }

        var probe = await healthProbe(ct);
        var target = $"{config.ForwardProxy.HealthCheckHost}:{config.ForwardProxy.HealthCheckPort}";
        return new DiagSection(
            "health",
            [
                probe.Ok
                    ? new DiagLine($"CONNECT-roundtrip {target}", $"OK in {probe.Milliseconds} ms ({probe.Detail})", DiagMark.Good)
                    : new DiagLine($"CONNECT-roundtrip {target}", $"failed in {probe.Milliseconds} ms: {probe.Detail}", DiagMark.Bad),
            ]);
    }

    private static DiagSection GuardsSection(LocalBanStatus ban, EgressPolicyStatus policy)
    {
        var policyLine = policy.State switch
        {
            GuardState.On => new DiagLine(
                "egress policy",
                $"egressProxyUrl={policy.CurrentUrl}, disableWslSessions={policy.DisableWslSessions}",
                DiagMark.Good),
            GuardState.Off => new DiagLine("egress policy", "not set (Claude may go direct)", DiagMark.Bad),
            GuardState.Partial => new DiagLine(
                "egress policy",
                $"{policy.CurrentUrl} (expected {policy.ExpectedUrl})",
                DiagMark.Warn),
            _ => new DiagLine("egress policy", policy.Detail ?? "unknown", DiagMark.Neutral),
        };

        var banLine = ban.State switch
        {
            GuardState.On => new DiagLine(
                "local ban",
                $"hosts: {ban.HostsEntries} names, NRPT: {ban.NrptNamespaces.Count} namespaces, firewall: {ban.FirewallRules} rules",
                DiagMark.Good),
            GuardState.Partial => new DiagLine("local ban", $"partial ({ban.Detail ?? $"hosts {ban.HostsEntries}, NRPT {ban.NrptRules}, firewall {ban.FirewallRules}"})", DiagMark.Warn),
            GuardState.Off => new DiagLine("local ban", "off - direct access is open", DiagMark.Bad),
            _ => new DiagLine("local ban", ban.Detail ?? "unknown", DiagMark.Neutral),
        };

        return new DiagSection("guards", [policyLine, banLine]);
    }

    /// <summary>
    /// Сторож автостопа виден извне только как процесс `pnr watchdog` (в GUI он живёт внутри
    /// окна приложения и по командной строке не отличается) — строка честно об этом говорит.
    /// </summary>
    private DiagSection WatchdogSection()
    {
        var watchdogs = processes.Find(WatchdogProcessName, "watchdog");
        var line = watchdogs.Count > 0
            ? new DiagLine("idle supervisor", $"pid {string.Join(", ", watchdogs.Select(w => w.Id))}", DiagMark.Good)
            : new DiagLine(
                "idle supervisor",
                "not found - the chain will not stop on its own (an in-app watchdog is only visible inside the GUI)",
                DiagMark.Warn);
        return new DiagSection("watchdog", [line]);
    }

    private DiagSection LogsSection()
    {
        var files = logs.List();
        var line = new DiagLine(
            "logs",
            files.Count == 0
                ? $"{logs.Directory} (no files yet)"
                : $"{logs.Directory} ({files.Count} files)");
        return new DiagSection("logs", [line]);
    }

    private static string DescribeOwners(IEnumerable<TcpListenerEntry> listeners)
    {
        var owners = listeners
            .GroupBy(l => l.ProcessId)
            .Select(g => g.First().ProcessName is { } name
                ? $"pid {g.Key} ({name})"
                : $"pid {g.Key}")
            .ToList();
        return owners.Count > 0 ? string.Join(", ", owners) : "no listener";
    }
}
