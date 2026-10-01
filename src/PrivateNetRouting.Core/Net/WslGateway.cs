using System.Net;
using System.Net.NetworkInformation;

namespace PrivateNetRouting.Core.Net;

/// <summary>
/// Шлюз WSL NAT: IPv4 адаптера "vEthernet (WSL...)" на стороне Windows. Адрес стабилен
/// на жизнь инстанса WSL, поэтому читается один раз на старте (с повторами до появления
/// адаптера) и дальше не отслеживается — как в прежней схеме.
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
