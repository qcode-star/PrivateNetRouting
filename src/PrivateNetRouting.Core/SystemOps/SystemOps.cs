using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>Проверка занятости локального TCP-порта (без прав администратора).</summary>
public interface IPortProbe
{
    bool IsListening(int port);
}

public sealed class TcpPortProbe : IPortProbe
{
    public bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);
}

public sealed record FoundProcess(int Id, string CommandLine);

/// <summary>
/// Поиск процессов по имени и подстроке командной строки (WMI). Так прежняя схема
/// опознаёт «свои» процессы цепочки — паттерном statedir/пайпа в командной строке.
/// </summary>
public interface IProcessFinder
{
    IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains);
}

public sealed class WmiProcessFinder : IProcessFinder
{
    public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains)
    {
        using var searcher = new ManagementObjectSearcher(
            new SelectQuery("Win32_Process", $"Name = '{processName.Replace("'", "''")}'"));
        var found = new List<FoundProcess>();
        foreach (var item in searcher.Get())
        {
            var commandLine = item["CommandLine"] as string ?? string.Empty;
            if (commandLine.Contains(commandLineContains, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new FoundProcess(Convert.ToInt32(item["ProcessId"]), commandLine));
            }
        }

        return found;
    }
}

/// <summary>Права текущего процесса (нужно ли элевировать старт демона и останов цепочки).</summary>
public static class WindowsPrivilege
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Утилиты запуска консольных процессов без окна.</summary>
public static class ProcessOps
{
    /// <summary>
    /// Запускает команду через cmd с файловым перенаправлением: файловые ручки принадлежат cmd
    /// и переживают выход нашего процесса — логи пишутся, пока жив потомок, а цепочка не зависит
    /// от жизни приложения. Так же устроены перенаправления в прежней схеме.
    /// </summary>
    public static Process? StartViaCmd(string commandLine, string? stdoutPath, string stderrPath)
    {
        var redirectOut = stdoutPath is null ? "NUL" : Quote(stdoutPath);
        var arguments = $"/c \"\"{commandLine}\" 1>{redirectOut} 2>{Quote(stderrPath)}\"";
        var info = new ProcessStartInfo("cmd.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(info);
    }

    public static string Quote(string value) => $"\"{value}\"";
}
