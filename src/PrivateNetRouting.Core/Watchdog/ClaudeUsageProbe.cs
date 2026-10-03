using System.Net.NetworkInformation;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>Установленные соединения на локальный порт (сторона слушателя прокси).</summary>
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
/// Признак занятости Claude или null при простое (порт Test-ClaudeInUse супервизора прежней
/// схемы). Проверяются по очереди: процессы claude* → установленные коннекты к прокси →
/// свежесть маркера активности (прикрывает Claude Code в WSL, у которого нет claude.exe).
/// </summary>
public interface IClaudeUsageProbe
{
    string? GetInUseReason(TimeSpan idleWindow);
}

/// <summary>
/// Отказ отдельного источника (например, WMI) трактуется как «сигнала нет» — так же, как
/// -ErrorAction SilentlyContinue в источнике; решение принимает первый ответивший источник.
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
            // маркер недоступен — просто нет сигнала
        }

        return null;
    }
}
