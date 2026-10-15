using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using System.Net;

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
        Assert.Equal("127.0.0.1:18083", config.Chain.Socks5.Upstream);
        Assert.Equal("127.0.0.1:18082", config.Chain.Tailscale.HttpListen);
        Assert.Equal(@"\\.\pipe\pnr-sidecar", config.Chain.Tailscale.Socket);
        Assert.True(config.ForwardProxy.BindWslGateway);
        Assert.True(config.ForwardProxy.DohDiscovery.Enabled); // the netmap fills the pool for a new user
        Assert.Empty(config.ForwardProxy.DohSeed); // optional; discovery is the source
        Assert.True(config.Ui.CheckUpdatesOnStart);
        Assert.False(config.Browser.IsConfigured); // the profile is created through the dialog, not by default
    }

    [Fact]
    public void FirstRunFileKeepsEnvironmentVariablesUnexpanded()
    {
        var config = AppConfigStore.LoadOrCreate(_path);

        var text = File.ReadAllText(_path);
        Assert.Contains("%LOCALAPPDATA%", text);
        Assert.DoesNotContain(Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%").Replace("\\", "\\\\"), text);
        Assert.DoesNotContain("%", config.Claude.Update.CacheDir); // the returned copy is expanded
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
              "forwardProxy": { "enabled": false },
              "chain": { "socks5": { "upstream": "127.0.0.1:11055", } }
            }
            """);

        var config = AppConfigStore.LoadOrCreate(_path);

        Assert.False(config.ForwardProxy.Enabled);
        Assert.Equal("127.0.0.1:11055", config.Chain.Socks5.Upstream);
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
        Assert.Empty(config.DirectTlds); // environment-specific: filled by the user config only
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
              "chain": { "mode": "http" }
            }
            """);
        Assert.Equal(ChainMode.Http, AppConfigStore.LoadOrCreate(_path).Chain.EffectiveMode);

        File.WriteAllText(_path, """
            {
              "chain": { "mode": "wireguard" }
            }
            """);
        Assert.Equal(ChainMode.Tailscale, AppConfigStore.LoadOrCreate(_path).Chain.EffectiveMode);
    }

    [Fact]
    public void AppsDefaultOnAndOneCanBeTurnedOff()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.True(config.Claude.Enabled); // both apps on for a new user
        Assert.True(config.Codex.Enabled);

        File.WriteAllText(_path, """
            {
              "claude": { "enabled": false }
            }
            """);

        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.False(loaded.Claude.Enabled); // only Claude hides; Codex stays
        Assert.True(loaded.Codex.Enabled);
    }

    [Fact]
    public void ForwardProxyEnabledDefaultsTrue()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.True(config.ForwardProxy.Enabled); // the local proxy runs by default

        File.WriteAllText(_path, """
            {
              "forwardProxy": {
                "enabled": false
              }
            }
            """);

        var loaded = AppConfigStore.LoadOrCreate(_path);
        Assert.False(loaded.ForwardProxy.Enabled);
    }

    [Fact]
    public void BanListsAreTheUnionOfBothApps()
    {
        var config = AppConfigStore.LoadOrCreate(_path);
        Assert.Contains("claude.ai", config.BanHosts);
        Assert.Contains("chatgpt.com", config.BanHosts);
        Assert.Contains("anthropic.com", config.Claude.BanTrees); // the probe www.anthropic.com lives under the tree...
        Assert.DoesNotContain("www.anthropic.com", config.Claude.BanHosts); // ...and stays absent from hosts
        Assert.Contains("openai.com", config.Codex.BanTrees);
        Assert.DoesNotContain("www.openai.com", config.Codex.BanHosts);
        Assert.Equal(config.Claude.BanHosts.Count + config.Codex.BanHosts.Count, config.BanHosts.Count);
        Assert.NotEmpty(config.Claude.FirewallSubnets);
        Assert.Empty(config.Codex.FirewallSubnets); // OpenAI rides on shared clouds
        Assert.All(config.Guard.BanProbes, probe => Assert.StartsWith("www.", probe));
    }

    [Fact]
    public void AppProxyEndpointMatrix()
    {
        // default shape: every client is pointed at the local listener
        var tailscale = new AppConfig();
        Assert.Equal(ChainMode.Tailscale, tailscale.Chain.EffectiveMode);
        Assert.Equal(tailscale.ForwardProxy.Listen, AppProxyEndpoint.Address(tailscale));
        Assert.Equal("http://127.0.0.1:18081", AppProxyEndpoint.Url(tailscale));
        Assert.Equal(18081, AppProxyEndpoint.Port(tailscale));
        Assert.False(AppProxyEndpoint.IsSkipped(tailscale));

        // the proxy off in the tailscale mode is a config error (the bring-up refuses), but the
        // endpoint math stays honest: IsSkipped stays false there
        tailscale.ForwardProxy.Enabled = false;
        Assert.False(AppProxyEndpoint.IsSkipped(tailscale));
        Assert.Equal(tailscale.ForwardProxy.Listen, AppProxyEndpoint.Address(tailscale));

        // socks5 with the proxy on — the local proxy still runs
        var socks5 = new AppConfig { Chain = { Mode = "socks5" } };
        Assert.False(AppProxyEndpoint.IsSkipped(socks5));
        Assert.Equal(socks5.ForwardProxy.Listen, AppProxyEndpoint.Address(socks5));

        // the skipped shape (socks5 + forwardProxy.enabled=false): everything points at
        // chain.socks5.upstream itself — the mixed inbound
        socks5.ForwardProxy.Enabled = false;
        socks5.Chain.Socks5.Upstream = "192.0.2.10:7080";
        Assert.True(AppProxyEndpoint.IsSkipped(socks5));
        Assert.Equal("192.0.2.10:7080", AppProxyEndpoint.Address(socks5));
        Assert.Equal("http://192.0.2.10:7080", AppProxyEndpoint.Url(socks5));
        Assert.Equal(7080, AppProxyEndpoint.Port(socks5));
        Assert.Equal(IPAddress.Parse("192.0.2.10"), AppProxyEndpoint.EndPoint(socks5).Address);

        // the http mode always runs no local proxy — forwardProxy.enabled is ignored there,
        // everything points at the provided HTTP proxy
        var http = new AppConfig { Chain = { Mode = "http" } };
        http.Chain.Http.Upstream = "192.0.2.20:3128";
        Assert.True(AppProxyEndpoint.IsSkipped(http));
        http.ForwardProxy.Enabled = true; // the flag does not bring the local proxy back
        Assert.True(AppProxyEndpoint.IsSkipped(http));
        Assert.Equal("192.0.2.20:3128", AppProxyEndpoint.Address(http));
        Assert.Equal("http://192.0.2.20:3128", AppProxyEndpoint.Url(http));
        Assert.Equal(3128, AppProxyEndpoint.Port(http));
        Assert.Equal(IPAddress.Parse("192.0.2.20"), AppProxyEndpoint.EndPoint(http).Address);
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

    [Fact]
    public void RepositoryTemplateParsesIntoTheExpectedDefaults()
    {
        // walk up from the test output to the repository root: the template is the contract
        // between the config schema and the users
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config", "appsettings.example.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var config = AppConfigStore.LoadOrCreate(Path.Combine(directory!.FullName, "config", "appsettings.example.json"));
        Assert.Equal(ChainMode.Tailscale, config.Chain.EffectiveMode);
        Assert.True(config.Claude.Enabled);
        Assert.True(config.Codex.Enabled);
        Assert.True(config.ForwardProxy.Enabled);
        Assert.Empty(config.BypassDomains);
        Assert.Empty(config.DirectTlds);
        Assert.Equal(["www.anthropic.com", "www.openai.com"], config.Guard.BanProbes);
        Assert.Equal("api.anthropic.com", config.ForwardProxy.HealthCheckHost);

        // every key of the template must land in the model: a misplaced key is silently ignored
        // by the default deserialization and its tuning would never take effect
        var strict = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        var text = File.ReadAllText(Path.Combine(directory.FullName, "config", "appsettings.example.json"));
        var strictConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(text, strict);
        Assert.NotNull(strictConfig);
        Assert.Equal(25, strictConfig.CloseWaitSeconds);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
