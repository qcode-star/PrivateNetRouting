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
    public void UpdatePatchesOnlyTheChangedKeyAndKeepsTheRestOfTheFile()
    {
        File.WriteAllText(_path, """
            {
              // the owner's note
              "ui": { "language": "auto" },
              "myCustomKey": { "keep": "Привет <&>" },
              "forwardProxy": { "listen": "127.0.0.1:19999" }
            }
            """);

        AppConfigStore.Update(config => config.Ui.Language = "ru", _path);

        var written = File.ReadAllText(_path);
        Assert.Contains("// the owner's note", written); // the comment survives the edit
        var root = ParseWithComments(written);
        Assert.Equal("ru", (string?)root["ui"]!["language"]);
        Assert.Equal("Привет <&>", (string?)root["myCustomKey"]!["keep"]); // unknown key kept, not escaped
        Assert.Equal("127.0.0.1:19999", (string?)root["forwardProxy"]!["listen"]);
        // no defaults were pinned into the file: sections the user never wrote stay absent
        Assert.Null(root["chain"]);
        Assert.Null(root["guard"]);
        Assert.Null(root["forwardProxy"]!["bindWslGateway"]);
        Assert.Equal(["ui", "myCustomKey", "forwardProxy"], root.Select(p => p.Key).ToArray());
    }

    private static System.Text.Json.Nodes.JsonObject ParseWithComments(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(
            json,
            documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })!.AsObject();

    /// <summary>A config copied from the commented template keeps every comment and its layout through a UI write.</summary>
    [Fact]
    public void UpdateEditsOnlyTheValueSpanAndKeepsCommentsAndFormatting()
    {
        const string original = """
            {
              // Interface.
              "ui": {
                // auto | en | ru
                "language":   "auto", /* spacing kept */
                "chainCardExpanded": true,
              },
              "watchdog": { "checkSeconds": 300 }, // inline section
              "forwardProxy": {
                "listen": "127.0.0.1:18081"
              }
            }
            """;
        File.WriteAllText(_path, original);

        AppConfigStore.Update(config => config.Ui.Language = "ru", _path);

        Assert.Equal(original.Replace("\"language\":   \"auto\"", "\"language\":   \"ru\""), File.ReadAllText(_path));
    }

    [Fact]
    public void UpdateEditsANestedKeyInPlace()
    {
        const string original = "{\r\n  // c\r\n  \"claude\": {\r\n    \"update\": {\r\n      \"chunkRetries\": 3 // retries\r\n    }\r\n  }\r\n}\r\n";
        File.WriteAllText(_path, original);

        AppConfigStore.Update(config => config.Claude.Update.ChunkRetries = 7, _path);

        Assert.Equal(original.Replace("\"chunkRetries\": 3", "\"chunkRetries\": 7"), File.ReadAllText(_path));
    }

    [Fact]
    public void UpdateAddsAMissingKeyMinimally()
    {
        const string original = """
            {
              // keep
              "ui": {
                "language": "en"
              },
              "watchdog": { "checkSeconds": 300 }
            }
            """;
        File.WriteAllText(_path, original);
        var defaults = new AppConfig();

        AppConfigStore.Update(
            config =>
            {
                config.Ui.ChainCardExpanded = !defaults.Ui.ChainCardExpanded; // a missing key of a multi-line object
                config.Watchdog.AutoStart = !defaults.Watchdog.AutoStart; // a missing key of an inline object
                config.Claude.Update.ChunkRetries = 9; // a whole missing section
            },
            _path);

        var written = File.ReadAllText(_path);
        Assert.Contains("// keep", written);
        Assert.Contains("\"language\": \"en\",\n    \"chainCardExpanded\": " + (!defaults.Ui.ChainCardExpanded).ToString().ToLowerInvariant(), written.Replace("\r\n", "\n"));
        Assert.Contains("{ \"checkSeconds\": 300, \"autoStart\": " + (!defaults.Watchdog.AutoStart).ToString().ToLowerInvariant() + " }", written);
        var root = ParseWithComments(written);
        Assert.Equal(9, (int)root["claude"]!["update"]!["chunkRetries"]!);
        Assert.Single(root["claude"]!.AsObject()); // only the changed leaf, no defaults pinned
        var reloaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal(!defaults.Ui.ChainCardExpanded, reloaded.Ui.ChainCardExpanded);
        Assert.Equal(!defaults.Watchdog.AutoStart, reloaded.Watchdog.AutoStart);
        Assert.Equal(9, reloaded.Claude.Update.ChunkRetries);
    }

    /// <summary>A path the text cannot hold surgically is refused, so Update falls back to the full rewrite.</summary>
    [Fact]
    public void TextPatchRefusesAPathThroughANonObject()
    {
        Assert.Throws<InvalidDataException>(() => JsonTextPatch.Apply(
            "{ \"a\": 1 }", [new JsonTextPatch.Edit(["a", "b"], System.Text.Json.Nodes.JsonValue.Create(2))]));
        Assert.Equal(
            "{ \"A\": 2 /* c */ }",
            JsonTextPatch.Apply("{ \"A\": 1 /* c */ }", [new JsonTextPatch.Edit(["a"], System.Text.Json.Nodes.JsonValue.Create(2))]));
        Assert.Equal(
            "{ \"a\": 1, \"a\": 3 }", // of duplicates the last one is what the loader keeps
            JsonTextPatch.Apply("{ \"a\": 1, \"a\": 2 }", [new JsonTextPatch.Edit(["a"], System.Text.Json.Nodes.JsonValue.Create(3))]));
    }

    [Fact]
    public void UpdateThatChangesNothingDoesNotTouchTheFile()
    {
        const string original = "{\n  // keep me\n  \"ui\": { \"language\": \"ru\" }\n}\n";
        File.WriteAllText(_path, original);

        AppConfigStore.Update(config => config.Ui.Language = "ru", _path);

        Assert.Equal(original, File.ReadAllText(_path)); // the comment survives a no-op
    }

    [Fact]
    public void UpdateWritesAKeyThatWasOnlyADefaultBefore()
    {
        File.WriteAllText(_path, "{ \"ui\": { } }");

        AppConfigStore.Update(config => config.Ui.ChainCardExpanded = !config.Ui.ChainCardExpanded, _path);

        var reloaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal(!new AppConfig().Ui.ChainCardExpanded, reloaded.Ui.ChainCardExpanded);
        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_path))!["chain"]);
    }

    // ----- validation: per-key messages instead of a crash deep in the composition -----

    private string LoadError(string json)
    {
        File.WriteAllText(_path, json);
        return Assert.Throws<InvalidDataException>(() => AppConfigStore.LoadOrCreate(_path)).Message;
    }

    [Fact]
    public void ValidationNamesTheKeyOfAHostNameWherePortAndIpAreParsed()
    {
        var message = LoadError("""{ "forwardProxy": { "listen": "localhost:18081" } }""");

        Assert.Contains("forwardProxy.listen", message);
        Assert.Contains("IP:port", message);
    }

    [Fact]
    public void ValidationRejectsPortsOutOfRangeAndTheEmptyHttpUpstreamOfTheHttpMode()
    {
        Assert.Contains("chain.socks5.upstream", LoadError("""{ "chain": { "mode": "socks5", "socks5": { "upstream": "127.0.0.1:99999" } } }"""));
        var http = LoadError("""{ "chain": { "mode": "http" } }""");
        Assert.Contains("chain.http.upstream", http);
        Assert.DoesNotContain("socks5", http); // the error is about the http mode, not "socks5: upstream must be an IP"
    }

    [Fact]
    public void ValidationChecksOnlyTheEndpointsOfTheModeInUse()
    {
        // the template's empty HTTP upstream does not matter to a socks5 setup
        File.WriteAllText(_path, """{ "chain": { "mode": "socks5" } }""");

        Assert.Equal(ChainMode.Socks5, AppConfigStore.LoadOrCreate(_path).Chain.EffectiveMode);
    }

    [Fact]
    public void ValidationRejectsNullSectionsAndListsAndNonsenseTimings()
    {
        Assert.Contains("ui: must not be null", LoadError("""{ "ui": null }"""));
        Assert.Contains("bypassDomains: must not be null", LoadError("""{ "bypassDomains": null }"""));
        Assert.Contains("forwardProxy.dohSeed: must not be null", LoadError("""{ "forwardProxy": { "dohSeed": null } }"""));

        var timings = LoadError("""{ "watchdog": { "checkSeconds": 0 }, "claude": { "update": { "chunkBytes": 0 } }, "restartGraceSeconds": 0 }""");
        Assert.Contains("watchdog.checkSeconds", timings);
        Assert.Contains("claude.update.chunkBytes", timings);
        Assert.Contains("restartGraceSeconds", timings); // every problem at once, not one per start
    }

    [Fact]
    public void ValidationRejectsNonsenseDohTimings()
    {
        var timings = LoadError(
            """{ "forwardProxy": { "dohRefreshSeconds": 0, "dohProbeTimeoutSeconds": -1, "dohDeadCooldownSeconds": 0, "cacheTtlMinSeconds": 0 } }""");

        Assert.Contains("forwardProxy.dohRefreshSeconds", timings);
        Assert.Contains("forwardProxy.dohProbeTimeoutSeconds", timings);
        Assert.Contains("forwardProxy.dohDeadCooldownSeconds", timings);
        Assert.Contains("forwardProxy.cacheTtlMinSeconds", timings);
    }

    [Fact]
    public void ValidationRejectsInvertedCacheTtlBounds()
    {
        var message = LoadError("""{ "forwardProxy": { "cacheTtlMinSeconds": 600, "cacheTtlMaxSeconds": 60 } }""");

        Assert.Contains("forwardProxy.cacheTtlMaxSeconds", message);
        Assert.Contains("cacheTtlMinSeconds", message); // the error names both keys
    }

    [Fact]
    public void ValidationRejectsAChainSocketThatWouldChangeTheCommandLine()
    {
        var config = new AppConfig();
        config.Chain.Tailscale.Socket = @"\\.\pipe\x"" & echo pwned";

        var errors = AppConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.StartsWith("chain.tailscale.socket", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationRejectsChainPathsThatAreRelativeOrCarryCmdMetacharacters()
    {
        var config = new AppConfig();
        config.Chain.Tailscale.StateDir = @"relative\sidecar";
        config.Chain.Tailscale.TailscaledPath = @"C:\tools\tailscaled.exe & calc";
        config.Chain.Tailscale.CliPath = @"""C:\tools\tailscale.exe""";

        var errors = AppConfigValidator.Validate(config);

        Assert.Contains(
            errors,
            error => error.StartsWith("chain.tailscale.stateDir", StringComparison.Ordinal) && error.Contains("absolute"));
        Assert.Contains(
            errors,
            error => error.StartsWith("chain.tailscale.tailscaledPath", StringComparison.Ordinal) && error.Contains("metacharacter"));
        Assert.Contains(errors, error => error.StartsWith("chain.tailscale.cliPath", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationSkipsTheUnusedTailscalePathsInAnExternalMode()
    {
        var config = new AppConfig { Chain = { Mode = "socks5" } };
        config.Chain.Tailscale.Socket = "";
        config.Chain.Tailscale.StateDir = "";
        config.Chain.Socks5.Upstream = "192.0.2.10:1080";

        Assert.Empty(AppConfigValidator.Validate(config));
    }

    [Fact]
    public void AMaliciousSocketIsAConfigErrorNamingTheKey()
    {
        var message = LoadError("""{ "chain": { "tailscale": { "socket": "\\\\.\\pipe\\x\" & echo pwned" } } }""");

        Assert.Contains("chain.tailscale.socket", message);
        Assert.Contains("named-pipe", message);
    }

    [Fact]
    public void ReloadKeepsTheRunningProxyAndChainButCopiesTheWatchdogInPlace()
    {
        var running = AppConfigStore.LoadOrCreate(_path);
        var watchdog = running.Watchdog;
        var forwardProxy = running.ForwardProxy;
        var chain = running.Chain;
        File.WriteAllText(_path, """
            { "forwardProxy": { "listen": "127.0.0.1:19999" },
              "chain": { "mode": "socks5" },
              "watchdog": { "autoStart": false, "checkSeconds": 77 },
              "ui": { "language": "ru" } }
            """);

        var restartRequired = AppConfigStore.ReloadInto(running, _path);

        Assert.True(restartRequired);
        Assert.Same(forwardProxy, running.ForwardProxy);
        Assert.Same(chain, running.Chain);
        Assert.Equal("127.0.0.1:18081", running.ForwardProxy.Listen);
        Assert.Same(watchdog, running.Watchdog); // the stopper supervisor holds this very object
        Assert.False(running.Watchdog.AutoStart);
        Assert.Equal(77, running.Watchdog.CheckSeconds);
        Assert.Equal("ru", running.Ui.Language);
    }

    [Fact]
    public void ReloadAppliesTheUpdaterSectionsInPlaceForCapturedReferences()
    {
        var running = AppConfigStore.LoadOrCreate(_path);
        var capturedClaudeUpdate = running.Claude.Update;
        var capturedCodexUpdate = running.Codex.Update;
        File.WriteAllText(_path, """
            { "claude": { "update": { "chunkBytes": 1048576, "parallelChunks": 7 } },
              "codex": { "update": { "updateTimeoutSeconds": 555 } } }
            """);

        var restartRequired = AppConfigStore.ReloadInto(running, _path);

        Assert.False(restartRequired);
        Assert.Same(capturedClaudeUpdate, running.Claude.Update); // the helpers hold this very instance
        Assert.Same(capturedCodexUpdate, running.Codex.Update);
        Assert.Equal(1048576, capturedClaudeUpdate.ChunkBytes);
        Assert.Equal(7, capturedClaudeUpdate.ParallelChunks);
        Assert.Equal(555, capturedCodexUpdate.UpdateTimeoutSeconds);
    }

    [Fact]
    public void ReloadOfAnUnchangedProxyAndChainNeedsNoRestart()
    {
        var running = AppConfigStore.LoadOrCreate(_path);

        Assert.False(AppConfigStore.ReloadInto(running, _path));
    }

    [Fact]
    public async Task UpdateAsyncAppliesTheMutationOffTheCallerThread()
    {
        File.WriteAllText(_path, """{ "ui": { "language": "en" } }""");

        await AppConfigStore.UpdateAsync(config => config.Ui.Language = "ru", _path);

        Assert.Equal("ru", AppConfigStore.LoadOrCreate(_path).Ui.Language);
    }

    [Fact]
    public void TheDefaultConfigIsValid()
        => Assert.Empty(AppConfigValidator.Validate(new AppConfig()));

    [Fact]
    public void ForwardProxyListenIsValidatedOnlyWhereTheLocalProxyRuns()
    {
        static AppConfig With(string mode, bool enabled, string listen)
        {
            var config = new AppConfig();
            config.Chain.Mode = mode;
            config.Chain.Socks5.Upstream = "192.0.2.10:1080";
            config.Chain.Http.Upstream = "192.0.2.10:3128";
            config.ForwardProxy.Enabled = enabled;
            config.ForwardProxy.Listen = listen;
            return config;
        }

        static bool ListenFlagged(AppConfig config) =>
            AppConfigValidator.Validate(config).Any(error => error.StartsWith("forwardProxy.listen", StringComparison.Ordinal));

        // the local proxy runs: a bad value or port 0 (clients could not connect to it) is rejected
        Assert.True(ListenFlagged(With("tailscale", true, "localhost:18081")));
        Assert.True(ListenFlagged(With("socks5", true, "127.0.0.1:0")));
        Assert.True(ListenFlagged(With("tailscale", true, "")));

        // the skipped shapes never read it
        Assert.False(ListenFlagged(With("http", true, "")));
        Assert.False(ListenFlagged(With("http", false, "not-an-endpoint")));
        Assert.False(ListenFlagged(With("socks5", false, "127.0.0.1:0")));

        // the upstream clients point at instead keeps its port check
        var httpZero = With("http", true, "");
        httpZero.Chain.Http.Upstream = "192.0.2.10:0";
        Assert.Contains(AppConfigValidator.Validate(httpZero), error => error.StartsWith("chain.http.upstream", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeadTimeoutHasItsOwnSettingAndMustBePositive()
    {
        var config = new AppConfig();
        Assert.Equal(30, config.Claude.Update.HeadTimeoutSeconds);

        config.Claude.Update.HeadTimeoutSeconds = 0;

        Assert.Contains(AppConfigValidator.Validate(config), error => error.Contains("claude.update.headTimeoutSeconds"));
    }

    [Fact]
    public void SaveReplacesTheFileInOneStepAndLeavesNoTempFile()
    {
        AppConfigStore.LoadOrCreate(_path);
        var config = AppConfigStore.LoadOrCreate(_path);
        config.Ui.Language = "ru";

        AppConfigStore.Save(config, _path);

        Assert.Equal("ru", AppConfigStore.LoadOrCreate(_path).Ui.Language);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".*.tmp"));
    }

    [Fact]
    public async Task ConcurrentUpdatesNeitherCollideNorLoseAChange()
    {
        AppConfigStore.LoadOrCreate(_path);

        await Task.WhenAll(
            Task.Run(() => AppConfigStore.Update(config => config.Ui.Language = "ru", _path)),
            Task.Run(() => AppConfigStore.Update(config => config.Ui.ChainCardExpanded = !new AppConfig().Ui.ChainCardExpanded, _path)),
            Task.Run(() => AppConfigStore.Update(config => config.Ui.GuardsCardExpanded = !new AppConfig().Ui.GuardsCardExpanded, _path)),
            Task.Run(() => AppConfigStore.Update(config => config.Watchdog.AutoStart = !new AppConfig().Watchdog.AutoStart, _path)));

        var reloaded = AppConfigStore.LoadOrCreate(_path);
        Assert.Equal("ru", reloaded.Ui.Language);
        Assert.Equal(!new AppConfig().Ui.ChainCardExpanded, reloaded.Ui.ChainCardExpanded);
        Assert.Equal(!new AppConfig().Ui.GuardsCardExpanded, reloaded.Ui.GuardsCardExpanded);
        Assert.Equal(!new AppConfig().Watchdog.AutoStart, reloaded.Watchdog.AutoStart);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".*.tmp"));
    }

    /// <summary>The existing file is replaced in place (File.Replace): its attributes survive the save.</summary>
    [Fact]
    public void SaveKeepsTheAttributesOfTheExistingFile()
    {
        AppConfigStore.LoadOrCreate(_path);
        File.SetAttributes(_path, File.GetAttributes(_path) | FileAttributes.Hidden);
        try
        {
            AppConfigStore.Update(config => config.Ui.Language = "ru", _path);

            Assert.True(File.GetAttributes(_path).HasFlag(FileAttributes.Hidden));
            Assert.Equal("ru", AppConfigStore.LoadOrCreate(_path).Ui.Language);
        }
        finally
        {
            File.SetAttributes(_path, FileAttributes.Normal);
        }
    }

    /// <summary>A symlinked config is written through to its target; the link stays a link.</summary>
    [Fact]
    public void SaveWritesThroughASymlinkedConfig()
    {
        var dir = Directory.CreateTempSubdirectory("pnr-config-link-").FullName;
        try
        {
            var target = Path.Combine(dir, "real.json");
            var link = Path.Combine(dir, "appsettings.json");
            AppConfigStore.LoadOrCreate(target);
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // creating symlinks needs the privilege (developer mode or admin): nothing to check here
            }

            AppConfigStore.Update(config => config.Ui.Language = "ru", link);

            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal("ru", AppConfigStore.LoadOrCreate(target).Ui.Language);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AFailedSaveKeepsThePreviousConfigIntact()
    {
        AppConfigStore.LoadOrCreate(_path);
        var before = File.ReadAllText(_path);
        var tempPattern = Path.GetFileName(_path) + ".*.tmp";

        // the config itself is held open without write or delete sharing (an editor, a scanner):
        // the temp file is written, then the replace over the real target fails
        using (var holder = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => AppConfigStore.Update(config => config.Ui.Language = "ru", _path));

            Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected: {error}");
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_path)!, tempPattern));
            Assert.Equal(before, File.ReadAllText(_path));
        }

        Assert.Equal(before, File.ReadAllText(_path));
        Assert.Equal(AppLanguage.Auto, AppConfigStore.LoadOrCreate(_path).Ui.Language);

        // the same for a full save
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => AppConfigStore.Save(new AppConfig { Ui = { Language = "ru" } }, _path));

            Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected: {error}");
        }

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_path)!, tempPattern));
        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public async Task LoadWaitsOutAShortSharingViolation()
    {
        AppConfigStore.LoadOrCreate(_path);
        // a reader-hostile holder (no sharing at all) that lets go after a moment
        var holder = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            holder.Dispose();
        });

        var config = AppConfigStore.LoadOrCreate(_path);

        Assert.NotNull(config);
        await release;
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
              "chain": { "mode": "http", "http": { "upstream": "192.0.2.10:3128" } }
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
