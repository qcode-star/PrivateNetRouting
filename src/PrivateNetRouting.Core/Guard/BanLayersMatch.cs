using System.Net;
using System.Net.Sockets;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// Compares the applied NRPT/firewall layers of the ban with the config: the status is "on" only
/// when they cover exactly the configured subtrees and subnets.
/// </summary>
internal static class BanLayersMatch
{
    /// <summary>The NRPT rule carries every subtree in both spellings: the apex and ".apex".</summary>
    public static bool SameNamespaces(IReadOnlyList<string> applied, IReadOnlyList<string> trees) =>
        new HashSet<string>(applied.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase)
            .SetEquals(trees.Where(t => !string.IsNullOrWhiteSpace(t)).SelectMany(t => new[] { t.Trim(), "." + t.Trim() }));

    public static bool SameSubnets(IReadOnlyList<string> applied, IReadOnlyList<string> configured) =>
        new HashSet<string>(applied.Select(NormalizeSubnet), StringComparer.OrdinalIgnoreCase)
            .SetEquals(configured.Where(s => !string.IsNullOrWhiteSpace(s)).Select(NormalizeSubnet));

    /// <summary>
    /// One spelling of a subnet: the firewall reads an IPv4 prefix back in the mask form
    /// (192.0.2.0/255.255.255.0), a single address without a prefix, and IPv6 in its canonical
    /// compression; anything that is not an address (a range, a keyword) is compared as written.
    /// </summary>
    internal static string NormalizeSubnet(string subnet)
    {
        var text = subnet.Trim();
        var slash = text.IndexOf('/');
        var addressPart = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressPart, out var address))
        {
            return text;
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        int prefix;
        if (slash < 0)
        {
            prefix = maxPrefix;
        }
        else
        {
            var prefixPart = text[(slash + 1)..];
            if (int.TryParse(prefixPart, out var bits))
            {
                prefix = bits;
            }
            else if (IPAddress.TryParse(prefixPart, out var mask) && MaskBits(mask) is { } maskBits)
            {
                prefix = maskBits;
            }
            else
            {
                return text;
            }
        }

        return $"{address}/{prefix}";
    }

    /// <summary>The prefix length of a contiguous mask; null for a mask with holes.</summary>
    private static int? MaskBits(IPAddress mask)
    {
        var bits = 0;
        var ended = false;
        foreach (var b in mask.GetAddressBytes())
        {
            for (var i = 7; i >= 0; i--)
            {
                var set = (b >> i & 1) == 1;
                if (set && ended)
                {
                    return null;
                }

                if (set)
                {
                    bits++;
                }
                else
                {
                    ended = true;
                }
            }
        }

        return bits;
    }
}
