using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

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
        Assert.Equal(AppLanguage.Auto, config.Ui.Language);
        Assert.Equal("127.0.0.1:18081", config.ForwardProxy.Listen);
        Assert.Equal("127.0.0.1:18083", config.ForwardProxy.Socks5Upstream);
        Assert.Equal("127.0.0.1:18082", config.Chain.DaemonHttpListen);
        Assert.Equal(@"\\.\pipe\pnr-sidecar", config.Chain.Socket);
        Assert.True(config.ForwardProxy.BindWslGateway);
        Assert.True(config.Ui.CheckUpdatesOnStart);
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
              // user config
              "forwardProxy": { "enabled": false, "socks5Upstream": "127.0.0.1:11055", }
            }
            """);

        var config = AppConfigStore.LoadOrCreate(_path);

        Assert.False(config.ForwardProxy.Enabled);
        Assert.Equal("127.0.0.1:11055", config.ForwardProxy.Socks5Upstream);
    }

    [Fact]
    public void LoggingLevelDefaultsToInfoAndParses()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal("info", config.Logging.Level);
        Assert.Equal(LogLevel.Info, config.Logging.EffectiveLevel);

        File.WriteAllText(_path, """
            {
              "logging": { "level": "debug" }
            }
            """);

        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal(LogLevel.Debug, loaded.Logging.EffectiveLevel);
    }

    [Fact]
    public void BrowserSectionDefaultsRoundTripAndExpand()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal(BrowserKind.Firefox, config.Browser.EffectiveType);
        Assert.Equal("https://claude.ai", config.Browser.Url);
        Assert.False(config.Browser.Private);
        Assert.Equal(["ru", "su", "by", "kz"], config.Browser.DirectTlds);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrivateNetRouting", "browser", "ff-profile"),
            config.Browser.Firefox.ProfileDir);

        config.Browser.Type = "chromium";
        config.Browser.Firefox.ProfileDir = @"%TEMP%\ff-profile";
        config.Browser.Chromium.Path = @"C:\fake\chrome.exe";
        AppConfigStore.Save(config, _path);

        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal(BrowserKind.Chromium, loaded.Browser.EffectiveType);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ff-profile"), loaded.Browser.Firefox.ProfileDir);
        Assert.Equal(@"C:\fake\chrome.exe", loaded.Browser.Chromium.Path);
    }

    [Fact]
    public void BrowserTypeUnknownValueFallsBackToFirefox()
    {
        File.WriteAllText(_path, """
            {
              "browser": { "type": "netscape" }
            }
            """);

        var config = AppConfigStore.LoadOrCreate(_path);

        Assert.Equal(BrowserKind.Firefox, config.Browser.EffectiveType);
    }

    [Fact]
    public void UpdateChangesUiLanguageWithoutExpandingVariables()
    {
        File.WriteAllText(_path, """
            {
              "forwardProxy": {
                "activityMarkerPath": "%TEMP%\\marker-file"
              }
            }
            """);

        AppConfigStore.Update(config => config.Ui.Language = "ru", _path);

        Assert.Contains("\"language\": \"ru\"", File.ReadAllText(_path));
        Assert.Contains("%TEMP%", File.ReadAllText(_path)); // %VAR% is stored unexpanded
        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal("ru", loaded.Ui.Language);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "marker-file"), loaded.ForwardProxy.ActivityMarkerPath);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
