using System.Net;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Diagnostics;

/// <summary>Summary line highlight: green — fine, amber — attention, red — problem.</summary>
public enum DiagMark
{
    Neutral,
    Good,
    Warn,
    Bad,
}

/// <summary>Summary line: technical name (English) and the ready-made value.</summary>
public sealed record DiagLine(string Name, string Value, DiagMark Mark = DiagMark.Neutral);

/// <summary>Summary section; Key is a stable identifier (chain/proxy/health/guards/watchdog), the GUI localizes the header by it.</summary>
public sealed record DiagSection(string Key, IReadOnlyList<DiagLine> Lines);

/// <summary>
/// State summary. ChainStopped is the edge case of the legacy proxy-status.ps1: neither the daemon
/// nor a forward proxy listener exists at all — that is normal while Claude is not needed (a short
/// message instead of the full picture).
/// </summary>
public sealed record DiagnosticsSummary(bool ChainStopped, IReadOnlyList<DiagSection> Sections);

/// <summary>
/// Diagnostics (port of proxy-status.ps1), read-only: chain processes and listener
/// owners (PID via the TCP table, like Get-NetTCPConnection), WSL bind of the proxy, counters,
/// activity marker age, detailed CONNECT roundtrip (time and first response line),
/// the core of the guards (policy + ban) and watchdog liveness. It starts and stops nothing.
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
    private const string WatchdogProcessName = "pnr.exe"; // CLI process of the `pnr watchdog` command

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

    /// <summary>The watch-ipn keeper is identified the same way as in SidecarKeeper: tailscale CLI + our pipe + watch-ipn in the cmdline.</summary>
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
                proxy.IsRunning
                    ? $"active {proxy.ActiveConnections}, total {proxy.TotalConnections}, failed {proxy.FailedConnections}"
                    : proxyListeners.Count > 0
                        ? "n/a (the proxy lives in another process)"
                        : "-"),
            ActivityLine(),
        };
        return new DiagSection("proxy", lines);
    }

    /// <summary>Age of the activity marker (mtime): how recently a proxied request happened.</summary>
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

    /// <summary>CONNECT roundtrip through the whole chain: time and first response line (the health-check port of the legacy scheme).</summary>
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
    /// The idle auto-stop watchdog is visible from outside only as a `pnr watchdog` process (in the GUI it
    /// lives inside the application window and is indistinguishable by command line) — the line says so honestly.
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
