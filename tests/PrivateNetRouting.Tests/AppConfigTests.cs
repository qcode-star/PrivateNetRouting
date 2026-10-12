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
        Assert.True(config.ForwardProxy.DohDiscovery.Enabled); // the netmap fills the pool for a new user
        Assert.Empty(config.ForwardProxy.DohSeed); // optional; discovery is the source
        Assert.True(config.Ui.CheckUpdatesOnStart);
        Assert.False(config.Browser.IsConfigured); // the profile is created through the dialog, not by default
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

        // The expectation goes through the same %VAR% expansion as the code: on some machines
        // %TEMP% is the 8.3 short form (CI runners) and Path.GetTempPath() returns the long one.
        var temp = Environment.ExpandEnvironmentVariables("%TEMP%");
        Assert.Equal(Path.Combine(temp, "marker-file"), config.ForwardProxy.ActivityMarkerPath);
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
        Assert.Null(config.Browser.Kind); // not created yet: no family, no paths
        Assert.False(config.Browser.IsConfigured);
        Assert.Equal(5, config.Browser.ProbeTimeoutSeconds);
        Assert.Equal(["ru", "su", "by", "kz"], config.Browser.DirectTlds);
        Assert.Equal("forward-proxy", config.Browser.Egress); // the application proxy by default
        Assert.Equal(BrowserEgressMode.ForwardProxy, config.Browser.EgressMode);

        config.Browser.Type = "chromium";
        config.Browser.Egress = "chain";
        config.Browser.ExecutablePath = @"%ProgramFiles%\Google\Chrome\Application\chrome.exe";
        config.Browser.ProfileDir = @"%TEMP%\chrome-profile";
        config.Browser.ShortcutPath = @"%TEMP%\PrivateNetRouting Chrome.lnk";
        AppConfigStore.Save(config, _path);

        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal(BrowserKind.Chrome, loaded.Browser.Kind);
        Assert.Equal("chain", loaded.Browser.Egress);
        Assert.Equal(BrowserEgressMode.Chain, loaded.Browser.EgressMode);
        Assert.True(loaded.Browser.IsConfigured);
        Assert.Equal(
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
            loaded.Browser.ExecutablePath);
        Assert.Equal(
            Path.Combine(Environment.ExpandEnvironmentVariables("%TEMP%"), "chrome-profile"),
            loaded.Browser.ProfileDir);
        Assert.Equal(
            Path.Combine(Environment.ExpandEnvironmentVariables("%TEMP%"), "PrivateNetRouting Chrome.lnk"),
            loaded.Browser.ShortcutPath);
    }

    [Fact]
    public void BrowserFamilyParserAcceptsBothSpellingsAndRefusesUnknown()
    {
        Assert.Equal(BrowserKind.Firefox, BrowserKindParser.ParseOrNull("Firefox"));
        Assert.Equal(BrowserKind.Chrome, BrowserKindParser.ParseOrNull("chrome"));
        Assert.Equal(BrowserKind.Chrome, BrowserKindParser.ParseOrNull("chromium"));
        Assert.Null(BrowserKindParser.ParseOrNull("netscape"));
        Assert.Null(BrowserKindParser.ParseOrNull(""));
        Assert.Null(BrowserKindParser.ParseOrNull(null));
    }

    [Fact]
    public void BrowserEgressParserTakesChainAndDefaultsToForwardProxy()
    {
        Assert.Equal(BrowserEgressMode.ForwardProxy, BrowserEgressModeParser.Parse("forward-proxy"));
        Assert.Equal(BrowserEgressMode.ForwardProxy, BrowserEgressModeParser.Parse(""));
        Assert.Equal(BrowserEgressMode.ForwardProxy, BrowserEgressModeParser.Parse(null));
        Assert.Equal(BrowserEgressMode.ForwardProxy, BrowserEgressModeParser.Parse("sing-box")); // unknown -> default
        Assert.Equal(BrowserEgressMode.Chain, BrowserEgressModeParser.Parse("chain"));
        Assert.Equal(BrowserEgressMode.Chain, BrowserEgressModeParser.Parse(" Chain "));
        Assert.Equal("forward-proxy", BrowserEgressModeParser.ToConfigValue(BrowserEgressMode.ForwardProxy));
        Assert.Equal("chain", BrowserEgressModeParser.ToConfigValue(BrowserEgressMode.Chain));
    }

    [Fact]
    public void BrowserProfileWithoutPathsIsNotConfigured()
    {
        // A hand-edited config that names a family but no paths: the launcher must offer the
        // profile dialog instead of starting something undefined.
        File.WriteAllText(_path, """
            {
              "browser": { "type": "firefox" }
            }
            """);

        var config = AppConfigStore.LoadOrCreate(_path);

        Assert.Equal(BrowserKind.Firefox, config.Browser.Kind);
        Assert.False(config.Browser.IsConfigured);
    }

    [Fact]
    public void ChainModeDefaultsToTailscaleAndParsesSocks5()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal(ChainMode.Tailscale, config.Chain.EffectiveMode);

        File.WriteAllText(_path, """
            {
              "chain": { "mode": "socks5" }
            }
            """);
        Assert.Equal(ChainMode.Socks5, AppConfigStore.LoadOrCreate(_path).Chain.EffectiveMode);

        File.WriteAllText(_path, """
            {
              "chain": { "mode": "wireguard" }
            }
            """);
        Assert.Equal(ChainMode.Tailscale, AppConfigStore.LoadOrCreate(_path).Chain.EffectiveMode);
    }

    [Fact]
    public void AppsSectionDefaultsOnAndTurnsOneAppOff()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.True(config.Apps.Claude); // both apps on for a new user
        Assert.True(config.Apps.Codex);

        File.WriteAllText(_path, """
            {
              "apps": { "claude": false }
            }
            """);

        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.False(loaded.Apps.Claude); // only Claude hides; Codex stays
        Assert.True(loaded.Apps.Codex);
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
        Assert.Equal(
            Path.Combine(Environment.ExpandEnvironmentVariables("%TEMP%"), "marker-file"),
            loaded.ForwardProxy.ActivityMarkerPath);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
