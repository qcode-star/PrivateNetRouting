using System.Net;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// The proxy endpoint the applications are pointed at (the "applications proxy"). It is
/// forwardProxy.listen — the local CONNECT proxy — in every shape but one: in the socks5 mode
/// with forwardProxy.enabled=false the local proxy is not run at all, and every client (the
/// launchers, the managed policy and the settings env, the Codex .env, the WSL sync, the updater
/// downloads, the health checks, the browser profile with browser.egress=forward-proxy) is
/// pointed straight at forwardProxy.externalAppsEndpoint — the mixed HTTP+SOCKS inbound of the
/// external upstream (e.g. sing-box), which does the same CONNECT-to-SOCKS translation the
/// local proxy would. The tailscale mode cannot work without the local proxy (DoH resolution +
/// dialing by IP through the sidecar; the sidecar's own proxies resolve names with the system
/// resolver) — a chain bring-up refuses with an explicit error. Both shapes speak HTTP CONNECT,
/// so the scheme is always http.
/// </summary>
public static class AppProxyEndpoint
{
    /// <summary>The local proxy does not run in this shape (socks5 mode + forwardProxy.enabled=false).</summary>
    public static bool IsSkipped(AppConfig config) =>
        config.Chain.EffectiveMode == ChainMode.Socks5 && !config.ForwardProxy.Enabled;

    /// <summary>The endpoint ("host:port") every client is pointed at.</summary>
    public static string Address(AppConfig config) =>
        IsSkipped(config) ? config.ForwardProxy.ExternalAppsEndpoint : config.ForwardProxy.Listen;

    /// <summary>The proxy URL for the clients ("http://host:port").</summary>
    public static string Url(AppConfig config) => "http://" + Address(config);

    /// <summary>The endpoint as an IP endpoint; a malformed config value surfaces as a format error.</summary>
    public static IPEndPoint EndPoint(AppConfig config) => Socks5Client.ParseEndPoint(Address(config));

    /// <summary>The port of the endpoint (the WSL sync bakes it, the watchdog counts connections on it).</summary>
    public static int Port(AppConfig config) => EndPoint(config).Port;
}
