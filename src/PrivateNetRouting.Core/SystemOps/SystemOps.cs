using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>Check whether a local TCP port is busy (without administrator rights).</summary>
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
/// Process search by name and command-line substring (WMI): "our" chain processes are identified
/// by a statedir/pipe pattern in the command line.
/// </summary>
public interface IProcessFinder
{
    IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains);

    /// <summary>Search by a substring in the process name (WQL LIKE); the command line is not filtered.</summary>
    IReadOnlyList<FoundProcess> FindByNameContains(string namePart);

    /// <summary>Process by PID with its command line; null — the process is gone.</summary>
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

/// <summary>Current process rights (whether to elevate the daemon start and the chain shutdown).</summary>
public static class WindowsPrivilege
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Utilities for launching console processes without a window.</summary>
public static class ProcessOps
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const int HandleFlagInherit = 0x1;
    private static bool _stdHandlesGuarded;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

    /// <summary>
    /// Marks this process's std handles non-inheritable (once, idempotent). Without it every
    /// spawned child receives copies of them, and a child that outlives us — the daemon's cmd
    /// wrapper, a detached worker — pins the caller's redirected stdout forever: `pnr chain up`
    /// under PowerShell capture or a pipe never completes even though the command has finished.
    /// </summary>
    public static void KeepStdHandlesFromChildren()
    {
        if (_stdHandlesGuarded)
        {
            return;
        }

        _stdHandlesGuarded = true;
        foreach (var id in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            var handle = GetStdHandle(id);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                SetHandleInformation(handle, HandleFlagInherit, 0);
            }
        }
    }

    /// <summary>
    /// Launches a command via cmd with file redirection: the file handles belong to cmd
    /// and survive the exit of our process — logs are written while the child is alive, and the chain does not depend
    /// on the application's lifetime. The cmd process itself gets fresh std handles: with
    /// inherited ones a long-lived cmd (it waits for the child) pins the caller's redirected
    /// stdout forever. Nothing writes to these handles: the inner command redirects its own
    /// output to files/NUL.
    /// </summary>
    public static Process? StartViaCmd(string commandLine, string? stdoutPath, string stderrPath)
    {
        KeepStdHandlesFromChildren();
        return Process.Start(new ProcessStartInfo("cmd.exe", BuildCmdArguments(commandLine, stdoutPath, stderrPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
    }

    /// <summary>
    /// The form /c "…1&gt;out 2&gt;err": the outer quote paired with the command's own quote gives
    /// the canonical ""cmd" args" — cmd strips the first and last quotes, the inner ones are preserved.
    /// Double wrapping broke paths with spaces ("""C:\Program …" is not a command).
    /// </summary>
    internal static string BuildCmdArguments(string commandLine, string? stdoutPath, string stderrPath)
    {
        var redirectOut = stdoutPath is null ? "NUL" : Quote(stdoutPath);
        return $"/c \"{commandLine} 1>{redirectOut} 2>{Quote(stderrPath)}\"";
    }

    public static string Quote(string value) => $"\"{value}\"";
}
