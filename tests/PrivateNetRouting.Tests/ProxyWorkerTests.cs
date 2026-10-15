using PrivateNetRouting.Core.Net.Proxy;

namespace PrivateNetRouting.Tests;

public class ProxyWorkerTests
{
    [Fact]
    public void TakesTheListenAddressTheSpawnerWaitsOn()
    {
        var listen = ProxyWorker.ParseListenOverride(["--proxy-worker", "--listen", "127.0.0.1:18999"]);

        Assert.Equal("127.0.0.1:18999", listen);
    }

    [Theory]
    [InlineData("--proxy-worker")]
    [InlineData("--proxy-worker", "--listen")]                          // no value
    [InlineData("--proxy-worker", "--listen", "not-an-endpoint")]       // not an IP:port
    [InlineData("--proxy-worker", "--listen", "127.0.0.1:99999999999")] // port out of range
    public void KeepsTheConfigValueWhenThereIsNoUsableOverride(params string[] args)
    {
        Assert.Null(ProxyWorker.ParseListenOverride(args));
    }

    [Fact]
    public void NoArgumentsIsNoOverride()
    {
        Assert.Null(ProxyWorker.ParseListenOverride(null));
    }
}
