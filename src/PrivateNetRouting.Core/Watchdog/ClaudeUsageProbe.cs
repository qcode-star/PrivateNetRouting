using System.Net.NetworkInformation;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>Established connections to the local port (the listener side of the proxy).</summary>
public interface IEstablishedConnectionCounter
{
    int CountOnLocalPort(int port);
}

public sealed class TcpEstablishedConnectionCounter : IEstablishedConnectionCounter
{
    public int CountOnLocalPort(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpConnections()
            .Count(connection => connection.LocalEndPoint.Port == port && connection.State == TcpState.Established);
}

/// <summary>
/// A sign that Claude is busy, or null when idle. Checked in order: claude*/codex processes ->
/// established connections to the proxy -> established connections to the chain's egress listener
/// -> freshness of the activity marker (covers Claude Code in WSL, which has no claude.exe).
/// The codex process name keeps the chain alive while the Codex desktop app is up (its traffic
/// also counts through the connection check, but an idle open app must not let the chain die under
/// it). The egress listener carries the browser profile: it goes straight to the chain's egress,
/// past the application proxy, and a chain stopped under an open browser would cut it off.
/// </summary>
public interface IClaudeUsageProbe
{
    string? GetInUseReason(TimeSpan idleWindow);
}

/// <param name="proxyPort">Local port of the application's forward proxy (its clients are counted here).</param>
/// <param name="egressPort">Local port of the chain's own egress listener (chain.daemonSocksListen); 0 — the mode has no local egress to guard (socks5).</param>
public sealed class ClaudeUsageProbe(
    IProcessFinder processes,
    IEstablishedConnectionCounter connections,
    int proxyPort,
    string activityMarkerPath,
    string codexProcessName = "",
    int egressPort = 0) : IClaudeUsageProbe
{
    public string? GetInUseReason(TimeSpan idleWindow)
    {
        var processParts = new List<string>();
        var claudeCount = CountSafe(() => processes.FindByNameContains("claude").Count);
        if (claudeCount > 0)
        {
            processParts.Add($"claude processes: {claudeCount}");
        }

        if (codexProcessName.Length > 0)
        {
            var codexCount = CountSafe(() => processes.FindByNameContains(codexProcessName).Count);
            if (codexCount > 0)
            {
                processParts.Add($"codex processes: {codexCount}");
            }
        }

        if (processParts.Count > 0)
        {
            return string.Join("; ", processParts);
        }

        int connectionCount;
        try
        {
            connectionCount = connections.CountOnLocalPort(proxyPort);
        }
        catch (Exception)
        {
            connectionCount = 0;
        }

        if (connectionCount > 0)
        {
            return $"established connections to {proxyPort}: {connectionCount}";
        }

        if (egressPort > 0)
        {
            var egressConnections = CountSafe(() => connections.CountOnLocalPort(egressPort));
            if (egressConnections > 0)
            {
                return $"established connections to the egress {egressPort}: {egressConnections}";
            }
        }

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

        return null;
    }

    private int CountSafe(Func<int> count)
    {
        try
        {
            return count();
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
