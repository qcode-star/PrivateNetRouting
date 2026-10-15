using System.Net;
using System.Runtime.InteropServices;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// One TCP listener: address, port and owner. The process name (if it is still alive) is resolved
/// by the table implementation — the consumer does not need a separate lookup by PID.
/// </summary>
public sealed record TcpListenerEntry(IPAddress Address, int Port, int ProcessId, string? ProcessName);

/// <summary>Listeners of a local TCP port with the owner PID (analog of Get-NetTCPConnection -State Listen).</summary>
public interface ITcpConnectionTable
{
    IReadOnlyList<TcpListenerEntry> GetListeners(int port);
}

/// <summary>
/// TCP connection table via iphlpapi GetExtendedTcpTable (class OWNER_PID_LISTENER):
/// gives the listener's owner PID without PowerShell and without administrator rights. IPv4 only —
/// all listeners of the scheme (daemon, forward proxy, WSL gateway) bind to IPv4 addresses.
/// </summary>
public sealed class TcpConnectionTable : ITcpConnectionTable
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpStateListen = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaxAttempts = 5;

    public IReadOnlyList<TcpListenerEntry> GetListeners(int port)
    {
        // the table can grow between the size query and the fill (ERROR_INSUFFICIENT_BUFFER): ask
        // again with the size the call reported, a few times
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < MaxAttempts && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidListener, 0);
                if (result == ErrorInsufficientBuffer)
                {
                    continue; // size now holds the required length
                }

                return result == 0 ? ReadListeners(buffer, port) : [];
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private static List<TcpListenerEntry> ReadListeners(IntPtr buffer, int port)
    {
        // MIB_TCPTABLE_OWNER_PID: dwNumEntries, then rows of 6 DWORDs:
        // state, localAddr, localPort, remoteAddr, remotePort, owningPid (network byte order)
        var entries = new List<TcpListenerEntry>();
        var count = Marshal.ReadInt32(buffer, 0);
        const int rowBytes = 24;
        for (var i = 0; i < count; i++)
        {
            var row = buffer + 4 + i * rowBytes;
            if (Marshal.ReadInt32(row, 0) != TcpStateListen)
            {
                continue;
            }

            var localPort = ntohs((ushort)Marshal.ReadInt32(row, 8));
            if (localPort != port)
            {
                continue;
            }

            var address = (uint)Marshal.ReadInt32(row, 4);
            var pid = Marshal.ReadInt32(row, 20);
            entries.Add(new TcpListenerEntry(new IPAddress(address), localPort, pid, ProcessNameOf(pid)));
        }

        return entries;
    }

    private static string? ProcessNameOf(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    [DllImport("ws2_32.dll")]
    private static extern ushort ntohs(ushort netShort);
}
