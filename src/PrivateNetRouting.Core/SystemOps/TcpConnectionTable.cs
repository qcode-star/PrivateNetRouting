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

    public IReadOnlyList<TcpListenerEntry> GetListeners(int port)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidListener, 0) != 0)
            {
                return [];
            }

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
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ProcessNameOf(int pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid).ProcessName;
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
