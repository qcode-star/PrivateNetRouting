using System.Net;
using System.Net.NetworkInformation;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>Established connections on the LOCAL end of the port: a listener side — of the local proxy, or of the chain's egress. An external endpoint is matched by the remote end instead (<see cref="CountToRemote"/>).</summary>
public interface IEstablishedConnectionCounter
{
    int CountOnLocalPort(int port);

    /// <summary>
    /// Established connections whose remote end is exactly this endpoint: the clients of an external
    /// (non-local) proxy, where the port alone would also match unrelated traffic to the same port
    /// number elsewhere. The default falls back to the port match.
    /// </summary>
    int CountToRemote(IPAddress address, int port) => CountOnLocalPort(port);
}

public sealed class TcpEstablishedConnectionCounter : IEstablishedConnectionCounter
{
    /// <summary>Only the LOCAL end: an unrelated outbound connection to a remote port of the same
    /// number is not our listener and proves nothing. Windows reports a loopback client and its
    /// listener as two rows — the listener-side row carries our port on the local end.</summary>
    public int CountOnLocalPort(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpConnections()
            .Count(connection => connection.State == TcpState.Established
                && connection.LocalEndPoint.Port == port);

    public int CountToRemote(IPAddress address, int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpConnections()
            .Count(connection => connection.State == TcpState.Established
                && connection.RemoteEndPoint.Port == port
                && connection.RemoteEndPoint.Address.Equals(address));
}

/// <summary>
/// A sign that Claude is busy, or null when idle. Checked in order: claude*/codex processes ->
/// established connections to the proxy -> established connections to the chain's egress listener
/// -> freshness of the activity marker (covers Claude Code in WSL, which has no claude.exe).
/// The codex process name keeps the chain alive while the Codex desktop app is up (its traffic
/// also counts through the connection check, but an idle open app must not let the chain die under
/// it). The egress listener carries the browser profile: it goes straight to the chain's egress,
/// past the application proxy, and a chain stopped under an open browser would cut it off.
/// The proxy port is the applications proxy endpoint — the local listener's port, or the external
/// endpoint's in the skipped shape. The marker is only counted when a local proxy exists to touch
/// it (a null path — the skipped shape, where nobody writes the marker).
/// </summary>
public interface IClaudeUsageProbe
{
    string? GetInUseReason(TimeSpan idleWindow);
}

/// <param name="proxyPort">Port of the applications proxy endpoint (its clients are counted here).</param>
/// <param name="activityMarkerPath">Activity marker file; null — the shape has no local proxy to touch it (skipped socks5).</param>
/// <param name="codexCommandLineMarker">Fragment of the Codex image path (its MSIX install folder): the process name alone is shared with unrelated applications (the ChatGPT desktop app); empty — the name decides.</param>
/// <param name="externalProxyAddress">Address of the external applications proxy (the skipped shapes): its clients are counted by the remote end; null — the proxy is local and the port decides.</param>
/// <param name="egressPort">Local port of the chain's own egress listener (chain.tailscale.socksListen); 0 — the mode has no local egress to guard (socks5).</param>
public sealed class ClaudeUsageProbe(
    IProcessFinder processes,
    IEstablishedConnectionCounter connections,
    int proxyPort,
    string? activityMarkerPath,
    string codexProcessName = "",
    int egressPort = 0,
    string codexCommandLineMarker = "",
    IPAddress? externalProxyAddress = null) : IClaudeUsageProbe
{
    private static readonly TimeSpan FutureMarkerTolerance = TimeSpan.FromMinutes(5);

    public string? GetInUseReason(TimeSpan idleWindow)
    {
        // a source that fails proves nothing: an idle verdict needs every source to have answered,
        // otherwise the chain is kept (the probe is retried on the next tick)
        var failures = new List<string>();
        var processParts = new List<string>();
        var claudeCount = Count("claude processes", () => processes.FindByNameContains("claude").Count, failures);
        if (claudeCount > 0)
        {
            processParts.Add($"claude processes: {claudeCount}");
        }

        if (codexProcessName.Length > 0)
        {
            var codexCount = Count("codex processes", () => CodexProcesses().Count, failures);
            if (codexCount > 0)
            {
                processParts.Add($"codex processes: {codexCount}");
            }
        }

        if (processParts.Count > 0)
        {
            return string.Join("; ", processParts);
        }

        var connectionCount = Count(
            "proxy connections",
            () => externalProxyAddress is null
                ? connections.CountOnLocalPort(proxyPort)
                : connections.CountToRemote(externalProxyAddress, proxyPort),
            failures);
        if (connectionCount > 0)
        {
            return $"established connections to {proxyPort}: {connectionCount}";
        }

        if (egressPort > 0)
        {
            var egressConnections = Count("egress connections", () => connections.CountOnLocalPort(egressPort), failures);
            if (egressConnections > 0)
            {
                return $"established connections to the egress {egressPort}: {egressConnections}";
            }
        }

        if (!string.IsNullOrEmpty(activityMarkerPath))
        {
            try
            {
                if (File.Exists(activityMarkerPath))
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(activityMarkerPath);
                    // a marker stamped in the future (the wall clock was stepped back) is no proof of
                    // activity: the next Touch rewrites it; a small skew still counts
                    if (age < idleWindow && age > -FutureMarkerTolerance)
                    {
                        return $"proxy activity {(int)age.TotalSeconds}s ago";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // marker unavailable — simply no signal
            }
        }

        return failures.Count > 0
            ? $"probe failed ({string.Join(", ", failures)}), assuming in use"
            : null;
    }

    private IReadOnlyList<FoundProcess> CodexProcesses()
    {
        var found = processes.FindByNameContains(codexProcessName);
        return codexCommandLineMarker.Length == 0
            ? found
            : found.Where(p => p.CommandLine.Contains(codexCommandLineMarker, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static int Count(string source, Func<int> count, List<string> failures)
    {
        try
        {
            return count();
        }
        catch (Exception)
        {
            failures.Add(source);
            return 0;
        }
    }
}
