using System.Net;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// The proxy endpoint the applications are pointed at (the "applications proxy"). It is
/// forwardProxy.listen — the local CONNECT proxy — in every shape but two where the local proxy
/// does not run at all: the socks5 mode with forwardProxy.enabled=false (every client — the
/// launchers, the managed policy and the settings env, the Codex .env, the WSL sync, the updater
/// downloads, the health checks, the browser profile with browser.egress=forward-proxy — is
/// pointed straight at chain.socks5.upstream, which must then answer HTTP CONNECT as well as
/// SOCKS5: a mixed inbound, e.g. sing-box, does the CONNECT-to-SOCKS translation itself), and
/// the http mode always (the provided chain.http.upstream speaks HTTP CONNECT natively, so
/// there is nothing for a local proxy to add — the flag forwardProxy.enabled is ignored there).
/// The tailscale mode cannot work without the local proxy (DoH resolution + dialing by IP
/// through the sidecar; the sidecar's own proxies resolve names with the system resolver) — a
/// chain bring-up refuses with an explicit error. Every shape speaks HTTP CONNECT, so the
/// scheme is always http.
/// </summary>
public static class AppProxyEndpoint
{
    /// <summary>The local proxy does not run in this shape (the http mode, or socks5 + forwardProxy.enabled=false).</summary>
    public static bool IsSkipped(AppConfig config) =>
        config.Chain.EffectiveMode == ChainMode.Http
        || (config.Chain.EffectiveMode == ChainMode.Socks5 && !config.ForwardProxy.Enabled);

    /// <summary>The endpoint ("host:port") every client is pointed at.</summary>
    public static string Address(AppConfig config) =>
        IsSkipped(config)
            ? (config.Chain.EffectiveMode == ChainMode.Http ? config.Chain.Http.Upstream : config.Chain.Socks5.Upstream)
            : config.ForwardProxy.Listen;

    /// <summary>The proxy URL for the clients ("http://host:port").</summary>
    public static string Url(AppConfig config) => "http://" + Address(config);

    /// <summary>The endpoint as an IP endpoint; a malformed config value surfaces as a format error.</summary>
    public static IPEndPoint EndPoint(AppConfig config) => Socks5Client.ParseEndPoint(Address(config));

    /// <summary>The port of the endpoint (the WSL sync bakes it, the watchdog counts connections on it).</summary>
    public static int Port(AppConfig config) => EndPoint(config).Port;
}
