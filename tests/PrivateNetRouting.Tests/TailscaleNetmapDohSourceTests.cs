using System.Net;
using PrivateNetRouting.Core.Net.Dns;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Netmap parsing for arbitrary tailscale DNS configs: DoH resolvers over http and https, by IP
/// literal or by name (resolved once at discovery, the name kept for TLS SNI); a resolver that
/// does not resolve is skipped without killing the rest.
/// </summary>
public class TailscaleNetmapDohSourceTests
{
    private static Task<IPAddress?> ResolveAsync(string host, CancellationToken ct) =>
        Task.FromResult(host == "resolver.example.test" ? IPAddress.Parse("203.0.113.99") : null);

    [Fact]
    public async Task ParsesIpLiteralsOfBothSchemes()
    {
        const string netmap = """
            {"DNS":{"Configs":[{"Resolvers":[
                {"Addr":"http://203.0.113.10:8480/dns-query"},
                {"Addr":"https://203.0.113.11:8443/dns-query"}]}]}}
            """;

        var endpoints = await TailscaleNetmapDohSource.ParseEndpointsAsync(netmap, ResolveAsync, CancellationToken.None);

        Assert.Equal(
            [
                new DohEndpoint(IPAddress.Parse("203.0.113.10"), 8480),
                new DohEndpoint(IPAddress.Parse("203.0.113.11"), 8443, Secure: true),
            ],
            endpoints);
    }

    [Fact]
    public async Task OmittedPortFallsBackToTheSchemeDefault()
    {
        const string netmap = """
            {"Resolvers":[
                {"Addr":"https://resolver.example.test/dns-query"},
                {"Addr":"http://203.0.113.10/dns-query"}]}
            """;

        var endpoints = await TailscaleNetmapDohSource.ParseEndpointsAsync(netmap, ResolveAsync, CancellationToken.None);

        Assert.Equal(
            [
                new DohEndpoint(IPAddress.Parse("203.0.113.99"), 443, Secure: true, HostName: "resolver.example.test"),
                new DohEndpoint(IPAddress.Parse("203.0.113.10"), 80),
            ],
            endpoints);
    }

    [Fact]
    public async Task ResolvesNamedResolversAndKeepsTheNameForSni()
    {
        const string netmap = """
            {"Resolvers":[{"Addr":"https://resolver.example.test:443/dns-query","BootstrapResolution":["203.0.113.1"]}]}
            """;

        var endpoints = await TailscaleNetmapDohSource.ParseEndpointsAsync(netmap, ResolveAsync, CancellationToken.None);

        var endpoint = Assert.Single(endpoints);
        Assert.True(endpoint.Secure);
        Assert.Equal("resolver.example.test", endpoint.HostName);
        Assert.Equal(IPAddress.Parse("203.0.113.99"), endpoint.Ip);
    }

    [Fact]
    public async Task UnresolvableResolverIsSkippedTheRestSurvives()
    {
        const string netmap = """
            {"Resolvers":[
                {"Addr":"https://unknown.example.test:443/dns-query"},
                {"Addr":"http://203.0.113.10:8480/dns-query"}]}
            """;

        var endpoints = await TailscaleNetmapDohSource.ParseEndpointsAsync(netmap, ResolveAsync, CancellationToken.None);

        Assert.Equal([new DohEndpoint(IPAddress.Parse("203.0.113.10"), 8480)], endpoints);
    }

    [Fact]
    public async Task Ipv6LiteralIsSkippedDialIsIpv4Only()
    {
        const string netmap = """{"Resolvers":[{"Addr":"http://[2001:db8::1]:8480/dns-query"}]}""";

        var endpoints = await TailscaleNetmapDohSource.ParseEndpointsAsync(netmap, ResolveAsync, CancellationToken.None);

        Assert.Empty(endpoints);
    }

    [Fact]
    public async Task DuplicateEntriesCollapse()
    {
        const string netmap = """
            {"Resolvers":[
                {"Addr":"http://203.0.113.10:8480/dns-query"},
                {"Addr":"http://203.0.113.10:8480/dns-query"}]}
            """;

        var endpoints = await TailscaleNetmapDohSource.ParseEndpointsAsync(netmap, ResolveAsync, CancellationToken.None);

        Assert.Equal([new DohEndpoint(IPAddress.Parse("203.0.113.10"), 8480)], endpoints);
    }

    [Fact]
    public async Task NonResolverEntriesAreIgnored()
    {
        const string netmap = """
            {"Peers":["http://203.0.113.10:8480/other"],"DNS":{"Configs":[{"Resolvers":[]}]},"ServiceAddr":"https://control.example.test:443"}
            """;

        var endpoints = await TailscaleNetmapDohSource.ParseEndpointsAsync(netmap, ResolveAsync, CancellationToken.None);

        Assert.Empty(endpoints);
    }

    [Fact]
    public async Task SeedParsingAcceptsIpLiteralsOfBothSchemesOnly()
    {
        Assert.True(DohEndpoint.TryParse("http://203.0.113.10:8480/dns-query", out var plain));
        Assert.False(plain!.Secure);
        Assert.True(DohEndpoint.TryParse("https://203.0.113.10:8443/dns-query", out var secure));
        Assert.True(secure!.Secure);
        Assert.True(DohEndpoint.TryParse("https://203.0.113.10/dns-query", out var defaultPort));
        Assert.Equal(443, defaultPort!.Port);
        // names belong to discovery (which resolves them); seeds are IP literals
        Assert.False(DohEndpoint.TryParse("https://resolver.example.test/dns-query", out _));
        Assert.False(DohEndpoint.TryParse("ftp://203.0.113.10:8480/dns-query", out _));
        Assert.False(DohEndpoint.TryParse("http://203.0.113.10:8480/other", out _));
    }
}
