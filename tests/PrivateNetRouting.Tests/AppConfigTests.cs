using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Tests;

public class AppConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pnr-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void LoadOrCreateCreatesFileWithDefaults()
    {
        Assert.False(File.Exists(_path));
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.True(File.Exists(_path));
        Assert.Equal("127.0.0.1:18082", config.ForwardProxy.Listen);
        Assert.Equal("127.0.0.1:18083", config.ForwardProxy.Socks5Upstream);
        Assert.Equal("127.0.0.1:18081", config.Chain.DaemonHttpListen);
        Assert.Equal(@"\\.\pipe\pnr-sidecar", config.Chain.Socket);
        Assert.True(config.ForwardProxy.BindWslGateway);
    }

    [Fact]
    public void RoundTripsChangedValues()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        config.ForwardProxy.Listen = "127.0.0.1:9999";
        config.ForwardProxy.DohSeed = ["http://203.0.113.10:8480/dns-query"];
        AppConfigStore.Save(config, _path);

        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal("127.0.0.1:9999", loaded.ForwardProxy.Listen);
        Assert.Equal(["http://203.0.113.10:8480/dns-query"], loaded.ForwardProxy.DohSeed);
    }

    [Fact]
    public void ExpandsEnvironmentVariablesInPaths()
    {
        File.WriteAllText(_path, """
            {
              "forwardProxy": {
                "listen": "127.0.0.1:18082",
                "activityMarkerPath": "%TEMP%\\marker-file"
              }
            }
            """);

        var config = AppConfigStore.LoadOrCreate(_path);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "marker-file"), config.ForwardProxy.ActivityMarkerPath);
        var expectedLogDir = Path.Combine(
            Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"), "PrivateNetRouting", "logs");
        Assert.Equal(expectedLogDir, config.Logging.Directory);
    }

    [Fact]
    public void AcceptsCommentsAndCamelCase()
    {
        File.WriteAllText(_path, """
            {
              // пользовательский конфиг
              "forwardProxy": { "enabled": false, "socks5Upstream": "127.0.0.1:11055", }
            }
            """);

        var config = AppConfigStore.LoadOrCreate(_path);

        Assert.False(config.ForwardProxy.Enabled);
        Assert.Equal("127.0.0.1:11055", config.ForwardProxy.Socks5Upstream);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
