using System.Net.NetworkInformation;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>Established connections on the port (either end): the listener side for a local proxy, the remote side when the endpoint belongs to an external process.</summary>
public interface IEstablishedConnectionCounter
{
    int CountOnLocalPort(int port);
}

public sealed class TcpEstablishedConnectionCounter : IEstablishedConnectionCounter
{
    public int CountOnLocalPort(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpConnections()
            .Count(connection => connection.State == TcpState.Established
                && (connection.LocalEndPoint.Port == port || connection.RemoteEndPoint.Port == port));
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
/// <param name="egressPort">Local port of the chain's own egress listener (chain.tailscale.socksListen); 0 — the mode has no local egress to guard (socks5).</param>
public sealed class ClaudeUsageProbe(
    IProcessFinder processes,
    IEstablishedConnectionCounter connections,
    int proxyPort,
    string? activityMarkerPath,
    string codexProcessName = "",
    int egressPort = 0) : IClaudeUsageProbe
{
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
            var codexCount = Count("codex processes", () => processes.FindByNameContains(codexProcessName).Count, failures);
            if (codexCount > 0)
            {
                processParts.Add($"codex processes: {codexCount}");
            }
        }

        if (processParts.Count > 0)
        {
            return string.Join("; ", processParts);
        }

        var connectionCount = Count("proxy connections", () => connections.CountOnLocalPort(proxyPort), failures);
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
                    if (age < idleWindow)
                    {
                        return $"proxy activity {(int)age.TotalSeconds}s ago";
                    }
                }
            }
            catch (IOException)
            {
                // marker unavailable — simply no signal
            }
        }

        return failures.Count > 0
            ? $"probe failed ({string.Join(", ", failures)}), assuming in use"
            : null;
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
