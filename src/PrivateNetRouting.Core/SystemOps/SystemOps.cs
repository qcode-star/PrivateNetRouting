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

    /// <summary>Поиск по подстроке в имени процесса (WQL LIKE), командная строка не фильтруется.</summary>
    IReadOnlyList<FoundProcess> FindByNameContains(string namePart);

    /// <summary>Процесс по PID с его командной строкой; null — процесса уже нет.</summary>
    FoundProcess? FindById(int processId);
}

public sealed class WmiProcessFinder : IProcessFinder
{
    public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains)
    {
        using var searcher = new ManagementObjectSearcher(
            new SelectQuery("Win32_Process", $"Name = '{processName.Replace("'", "''")}'"));
        return Search(searcher, commandLineContains);
    }

    public IReadOnlyList<FoundProcess> FindByNameContains(string namePart)
    {
        using var searcher = new ManagementObjectSearcher(
            new SelectQuery("Win32_Process", $"Name LIKE '%{namePart.Replace("'", "''")}%'"));
        return Search(searcher, commandLineContains: string.Empty);
    }

    public FoundProcess? FindById(int processId)
    {
        using var searcher = new ManagementObjectSearcher(
            new SelectQuery("Win32_Process", $"ProcessId = {processId}"));
        return Search(searcher, commandLineContains: string.Empty).FirstOrDefault();
    }

    private static IReadOnlyList<FoundProcess> Search(ManagementObjectSearcher searcher, string commandLineContains)
    {
        var found = new List<FoundProcess>();
        foreach (var item in searcher.Get())
        {
            var commandLine = item["CommandLine"] as string ?? string.Empty;
            if (string.IsNullOrEmpty(commandLineContains)
                || commandLine.Contains(commandLineContains, StringComparison.OrdinalIgnoreCase))
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
    public static Process? StartViaCmd(string commandLine, string? stdoutPath, string stderrPath) =>
        Process.Start(new ProcessStartInfo("cmd.exe", BuildCmdArguments(commandLine, stdoutPath, stderrPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });

    /// <summary>
    /// Форма /c "…1&gt;out 2&gt;err": внешняя кавычка в паре с собственной кавычкой команды даёт
    /// каноническое ""cmd" args" — cmd срывает первую и последнюю кавычки, внутренние сохраняются.
    /// Двойная оборачивка ломала пути с пробелами ("""C:\Program …" не является командой).
    /// </summary>
    internal static string BuildCmdArguments(string commandLine, string? stdoutPath, string stderrPath)
    {
        var redirectOut = stdoutPath is null ? "NUL" : Quote(stdoutPath);
        return $"/c \"{commandLine} 1>{redirectOut} 2>{Quote(stderrPath)}\"";
    }

    public static string Quote(string value) => $"\"{value}\"";
}
