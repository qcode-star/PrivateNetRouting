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
/// A sign that Claude is busy, or null when idle. Checked in order: claude* processes -> established connections to the proxy ->
/// freshness of the activity marker (covers Claude Code in WSL, which has no claude.exe).
/// </summary>
public interface IClaudeUsageProbe
{
    string? GetInUseReason(TimeSpan idleWindow);
}

/// <summary>
/// A failure of an individual source (e.g. WMI) is treated as "no signal" — just like
/// -ErrorAction SilentlyContinue in the source; the first source that responds decides.
/// </summary>
public sealed class ClaudeUsageProbe(
    IProcessFinder processes,
    IEstablishedConnectionCounter connections,
    int proxyPort,
    string activityMarkerPath) : IClaudeUsageProbe
{
    public string? GetInUseReason(TimeSpan idleWindow)
    {
        int processCount;
        try
        {
            processCount = processes.FindByNameContains("claude").Count;
        }
        catch (Exception)
        {
            processCount = 0;
        }

        if (processCount > 0)
        {
            return $"claude processes: {processCount}";
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
}
