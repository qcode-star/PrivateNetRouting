namespace PrivateNetRouting.Core.Configuration;

/// <summary>What carries the traffic; the chain bring-up shape follows it.</summary>
public enum ChainMode
{
    Tailscale = 0,
    Socks5 = 1,
}

public static class ChainModeParser
{
    /// <summary>Config string ("tailscale"/"socks5") to a mode; unknown — Tailscale.</summary>
    public static ChainMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "socks5" => ChainMode.Socks5,
        _ => ChainMode.Tailscale,
    };
}
