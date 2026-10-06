using System.Net;
using System.Net.NetworkInformation;

namespace PrivateNetRouting.Core.Net;

/// <summary>
/// WSL NAT gateway: the IPv4 of the "vEthernet (WSL...)" adapter on the Windows side. The address
/// is stable for the lifetime of the WSL instance, so it is read once at startup (with retries
/// until the adapter appears) and is not tracked further — as in the legacy scheme.
/// </summary>
public static class WslGateway
{
    public static IPAddress? FindIpv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.Name.StartsWith("vEthernet (WSL", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
}
