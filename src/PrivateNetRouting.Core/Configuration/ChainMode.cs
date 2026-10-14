namespace PrivateNetRouting.Core.Configuration;

/// <summary>What carries the traffic; the chain bring-up shape follows it.</summary>
public enum ChainMode
{
    Tailscale = 0,
    Socks5 = 1,

    /// <summary>A provided external HTTP proxy: no local proxy at all, every client is pointed at chain.http.upstream.</summary>
    Http = 2,
}

public static class ChainModeParser
{
    /// <summary>Config string ("tailscale"/"socks5"/"http") to a mode; unknown — Tailscale.</summary>
    public static ChainMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "socks5" => ChainMode.Socks5,
        "http" => ChainMode.Http,
        _ => ChainMode.Tailscale,
    };
}
