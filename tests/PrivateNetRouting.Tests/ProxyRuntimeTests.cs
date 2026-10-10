using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Composition-root decisions: which SOCKS5 upstream the proxy dials in each chain mode, and
/// the drift check behind the config reload's restart hint.
/// </summary>
public class ProxyRuntimeTests
{
    [Fact]
    public void TailscaleModeDialsTheDaemonListener()
    {
        var config = new AppConfig
        {
            Chain = new ChainConfig { Mode = "tailscale", DaemonSocksListen = "127.0.0.1:18083" },
            ForwardProxy = new ForwardProxyConfig
            {
                // a leftover from socks5-mode experiments: must not leak into the tailscale mode
                Socks5Upstream = "127.0.0.1:11055",
                Socks5UpstreamUser = "alice",
                Socks5UpstreamPassword = "s3cret",
            },
        };

        var (endpoint, user, password) = ProxyRuntime.ResolveUpstream(config);

        // the address comes from the daemon's own listener and needs no authentication
        Assert.Equal("127.0.0.1:18083", endpoint);
        Assert.Equal("", user);
        Assert.Equal("", password);
    }

    [Fact]
    public void Socks5ModeDialsTheExternalUpstream()
    {
        var config = new AppConfig
        {
            Chain = new ChainConfig { Mode = "socks5" },
            ForwardProxy = new ForwardProxyConfig
            {
                Socks5Upstream = "127.0.0.1:11055",
                Socks5UpstreamUser = "alice",
                Socks5UpstreamPassword = "s3cret",
            },
        };

        var (endpoint, user, password) = ProxyRuntime.ResolveUpstream(config);

        Assert.Equal("127.0.0.1:11055", endpoint);
        Assert.Equal("alice", user);
        Assert.Equal("s3cret", password);
    }
}
