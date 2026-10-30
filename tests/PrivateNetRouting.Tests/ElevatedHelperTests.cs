using System.IO.Pipes;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>
/// The tests that redirect the elevated helper's pinned directory to a temporary root: the
/// override is process-wide, so they must not run next to each other.
/// </summary>
[CollectionDefinition(nameof(ElevatedHelperPinnedRoot), DisableParallelization = true)]
public sealed class ElevatedHelperPinnedRoot;

/// <summary>
/// How "our" sidecar processes are recognized (a whole argument value, not a substring), the
/// command line of the elevated helper (the config of the asking user travels with it) and the
/// result-file round trip of the in-process path.
/// </summary>
[Collection(nameof(ElevatedHelperPinnedRoot))]
public class ElevatedHelperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pnr-helper-{Guid.NewGuid():N}");

    public ElevatedHelperTests()
    {
        Directory.CreateDirectory(_dir);
        ElevatedHelperTrust.PinnedRootOverride = _dir;
    }

    public void Dispose()
    {
        ElevatedHelperTrust.PinnedRootOverride = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The pinned elevated directory, redirected into the test's temporary root.</summary>
    private string PinnedDir => Path.Combine(_dir, "PrivateNetRouting", "elevated");

    // ----- P2-21: identification by a whole argument value -----

    [Theory]
    [InlineData(@"tailscaled.exe ""--statedir=C:\x\sidecar"" ""--socket=\\.\pipe\a""", @"C:\x\sidecar", true)]
    [InlineData(@"tailscaled.exe --statedir=C:\x\sidecar --socket=a", @"C:\x\sidecar", true)]
    [InlineData(@"tailscaled.exe ""--statedir=C:\x\sidecar-old""", @"C:\x\sidecar", false)]
    [InlineData(@"tailscaled.exe ""--statedir=C:\x\sidecar\nested""", @"C:\x\sidecar", false)]
    [InlineData(@"tailscaled.exe ""--statedir=D:\C:\x\sidecar""", @"C:\x\sidecar", false)]
    [InlineData(@"tailscale.exe ""--socket=\\.\pipe\pnr-sidecar-2"" debug watch-ipn", @"\\.\pipe\pnr-sidecar", false)]
    [InlineData(@"tailscale.exe ""--socket=\\.\pipe\PNR-SIDECAR"" debug watch-ipn", @"\\.\pipe\pnr-sidecar", true)]
    // an earlier near-miss must not hide a later exact occurrence
    [InlineData(@"x ""--a=C:\x\sidecar-old"" ""--b=C:\x\sidecar""", @"C:\x\sidecar", true)]
    public void ArgumentValueMatchIsWholeValueOnly(string commandLine, string value, bool expected) =>
        Assert.Equal(expected, ProcessMatch.ContainsArgumentValue(commandLine, value));

    [Fact]
    public void KeeperIgnoresAWatchIpnOfAnotherPipeWithTheSamePrefix()
    {
        var finder = new FakeFinderByName();
        finder.ByName["tailscale.exe"] =
        [
            new FoundProcess(11, @"tailscale.exe ""--socket=\\.\pipe\pnr-sidecar-old"" debug watch-ipn"),
        ];
        using var log = new FileLog(Path.Combine(_dir, "keeper.log"));
        var keeper = new SidecarKeeper(new ChainTailscaleConfig { Socket = @"\\.\pipe\pnr-sidecar" }, finder, log);

        Assert.False(keeper.IsRunning);

        finder.ByName["tailscale.exe"].Add(new FoundProcess(12, @"tailscale.exe ""--socket=\\.\pipe\pnr-sidecar"" debug watch-ipn"));
        Assert.True(keeper.IsRunning);
    }

    [Fact]
    public void ARecycledPidIsNotConfirmedForTheKill()
    {
        var finder = new FakeFinderByName();
        finder.ByName["tailscaled.exe"] = [new FoundProcess(7, @"tailscaled.exe ""--statedir=C:\x\sidecar""")];
        var scanned = finder.FindByArgument("tailscaled.exe", @"C:\x\sidecar").Single();
        Assert.True(finder.StillCarriesArgument(scanned, @"C:\x\sidecar"));

        // the pid now belongs to something else
        finder.ByName["tailscaled.exe"] = [new FoundProcess(7, "notepad.exe")];
        Assert.False(finder.StillCarriesArgument(scanned, @"C:\x\sidecar"));

        // or to nothing at all
        finder.ByName["tailscaled.exe"] = [];
        Assert.False(finder.StillCarriesArgument(scanned, @"C:\x\sidecar"));
    }

    /// <summary>The kill opens the process once, re-checks it under the held handle and kills that same process.</summary>
    [Fact]
    public void KillIfStillCarriesKillsOnlyAProcessThatIsStillOurs()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var finder = new FakeFinderByName();
            var scanned = new FoundProcess(child.Id, @"tailscaled.exe ""--statedir=C:\x\sidecar""");

            // the pid now reports a foreign command line: left alone
            finder.ByName["ping.exe"] = [new FoundProcess(child.Id, "ping.exe -n 60 127.0.0.1")];
            Assert.False(finder.KillIfStillCarries(scanned, @"C:\x\sidecar"));
            Assert.False(child.HasExited);

            // still ours: killed
            finder.ByName["ping.exe"] = [scanned];
            Assert.True(finder.KillIfStillCarries(scanned, @"C:\x\sidecar"));
            Assert.True(child.WaitForExit(5000));

            // gone: nothing to kill, no error
            Assert.False(finder.KillIfStillCarries(scanned, @"C:\x\sidecar"));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }

    // ----- P2-24: the helper gets the config of the asking user -----

    [Fact]
    public void HelperArgumentsCarryTheResultFileAndTheConfig()
    {
        var resultFile = Path.Combine(_dir, "elevated-start-daemon=0a1b2c3d.result");
        var configPath = Path.Combine(_dir, "with space", "appsettings.json");

        var line = ElevatedHelperArguments.Build(ElevatedChainCommands.StartDaemon, resultFile, configPath);

        Assert.StartsWith(ElevatedChainCommands.StartDaemon + " --result-file \"", line);
        Assert.EndsWith($"--config \"{configPath}\"", line);
        // what the helper parses back out of its argument vector (the shell has removed the quotes)
        string[] args = [ElevatedChainCommands.StartDaemon, "--result-file", resultFile, "--config", configPath];
        Assert.Equal(resultFile, ElevatedHelperArguments.ParseOption(args, ElevatedHelperArguments.ResultFileOption));
        Assert.Equal(configPath, ElevatedHelperArguments.ParseOption(args, ElevatedHelperArguments.ConfigOption));
    }

    [Fact]
    public void HelperArgumentsWithoutAConfigStayWithoutOne()
    {
        var line = ElevatedHelperArguments.Build(ElevatedChainCommands.StopChain, @"C:\s\r.result", null);

        Assert.DoesNotContain("--config", line);
        Assert.Null(ElevatedHelperArguments.ParseOption([ElevatedChainCommands.StopChain, "--result-file", @"C:\s\r.result"], "--config"));
        Assert.Null(ElevatedHelperArguments.ParseOption([ElevatedChainCommands.StopChain], "--result-file"));
    }

    // ----- the helper expands the config with the asking user's profile variables -----

    [Fact]
    public void HelperArgumentsCarryTheProfileVariablesAndParseBack()
    {
        var environment = new Dictionary<string, string>
        {
            ["APPDATA"] = @"C:\Users\asker\AppData\Roaming",
            ["LOCALAPPDATA"] = @"C:\Users\asker\AppData\Local",
            ["USERPROFILE"] = @"C:\Users\asker",
        };

        var line = ElevatedHelperArguments.Build(ElevatedChainCommands.StopChain, @"C:\s\r.result", @"C:\c\appsettings.json", environment);

        Assert.Contains(@"--env ""APPDATA=C:\Users\asker\AppData\Roaming""", line);
        Assert.Contains(@"--env ""USERPROFILE=C:\Users\asker""", line);
        // what the helper parses back out of its argument vector (the shell has removed the quotes)
        string[] args =
        [
            ElevatedChainCommands.StopChain, "--result-file", @"C:\s\r.result", "--config", @"C:\c\appsettings.json",
            "--env", @"APPDATA=C:\Users\asker\AppData\Roaming", "--env", @"LOCALAPPDATA=C:\Users\asker\AppData\Local",
            "--env", @"USERPROFILE=C:\Users\asker",
        ];
        var parsed = ElevatedHelperArguments.ToProfileEnvironment(ElevatedHelperArguments.ParseOptions(args, ElevatedHelperArguments.EnvOption));
        Assert.Equal(environment, parsed);
    }

    [Theory]
    [InlineData(@"PATH=C:\Windows")] // not a profile variable of ours
    [InlineData(@"APPDATA=relative\dir")]
    [InlineData(@"APPDATA=C:\x"" --config ""C:\evil.json")]
    [InlineData(@"APPDATA=C:\%TEMP%")]
    [InlineData(@"APPDATA=C:\x\")]
    [InlineData("APPDATA")]
    [InlineData(@"=C:\x")]
    public void TheHelperRefusesAnUnexpectedEnvironmentEntry(string entry) =>
        Assert.Throws<InvalidOperationException>(() => ElevatedHelperArguments.ToProfileEnvironment([entry]));

    [Fact]
    public void InvalidProfileValuesAreNotPassedToTheHelper()
    {
        var line = ElevatedHelperArguments.Build(
            ElevatedChainCommands.StopChain,
            @"C:\s\r.result",
            null,
            new Dictionary<string, string> { ["APPDATA"] = @"relative", ["PATH"] = @"C:\Windows" });

        Assert.DoesNotContain("--env", line);
    }

    [Fact]
    public void TheExplicitVariableMapWinsOverTheProcessEnvironment()
    {
        var map = new Dictionary<string, string> { ["APPDATA"] = @"C:\Users\asker\AppData\Roaming" };

        Assert.Equal(@"C:\Users\asker\AppData\Roaming\PrivateNetRouting\sidecar",
            ConfigEnvironment.Expand(@"%APPDATA%\PrivateNetRouting\sidecar", map));
        Assert.Equal(@"C:\Users\asker\AppData\Roaming\x", ConfigEnvironment.Expand(@"%appdata%\x", map)); // case-insensitive
        // a variable outside the map comes from the process itself
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\x"), ConfigEnvironment.Expand(@"%SystemRoot%\x", map));
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%APPDATA%\x"), ConfigEnvironment.Expand(@"%APPDATA%\x", null));
    }

    [Fact]
    public void TheHelperConfigIsExpandedWithTheAskingUsersVariables()
    {
        var configPath = Path.Combine(_dir, "appsettings.json");
        var map = new Dictionary<string, string>
        {
            ["APPDATA"] = @"C:\Users\asker\AppData\Roaming",
            ["LOCALAPPDATA"] = @"C:\Users\asker\AppData\Local",
        };

        var config = AppConfigStore.LoadOrCreate(configPath, map); // the fresh file keeps the %VAR% defaults

        Assert.StartsWith(@"C:\Users\asker\AppData\Roaming\", config.Chain.Tailscale.StateDir);
        Assert.StartsWith(@"C:\Users\asker\AppData\Local\", config.Logging.Directory);
        Assert.Contains("%APPDATA%", File.ReadAllText(configPath)); // nothing expanded is written back

        var reloaded = AppConfigStore.LoadOrCreate(configPath, map);
        Assert.Equal(config.Chain.Tailscale.StateDir, reloaded.Chain.Tailscale.StateDir);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Roaming\PrivateNetRouting\appsettings.json", true)]
    [InlineData(@"relative\appsettings.json", false)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", false)]
    [InlineData(@"\\server\share\config.json", false)]
    [InlineData(@"\\.\C:\config.json", false)]
    [InlineData("", false)]
    public void OnlyAnAbsoluteLocalJsonFileIsAcceptedAsTheHelperConfig(string path, bool expected) =>
        Assert.Equal(expected, ElevatedHelperTrust.IsValidConfigPath(path));

    [Theory]
    [InlineData(ElevatedChainCommands.StartDaemon, true)]
    [InlineData(ElevatedChainCommands.StopChain, true)]
    [InlineData("--elevated-guard-ban-on", true)]
    [InlineData("--launch", false)]
    [InlineData("", false)]
    public void HelperCommandsAreRecognized(string command, bool expected) =>
        Assert.Equal(expected, ElevatedHelperArguments.IsHelperCommand(command));

    [Fact]
    public async Task AHelperThatCannotLoadTheConfigReportsAFailureInsteadOfWritingNothing()
    {
        var resultFile = Path.Combine(PinnedDir, "elevated-start-daemon=0a1b2c3d.result");

        var code = await Task.Run(() => ElevatedChainCommands.Execute(
            ElevatedChainCommands.StartDaemon, resultFile, configPath: @"relative\not-absolute.json"));

        Assert.Equal(1, code);
        Assert.StartsWith("fail", File.ReadAllText(resultFile));
    }

    // ----- A-02: the helper writes only inside the pinned elevated directory -----

    [Fact]
    public void AWellNamedResultFileOutsideThePinnedDirectoryIsRefused()
    {
        Assert.True(ElevatedHelperTrust.IsValidResultFile(Path.Combine(PinnedDir, "elevated-start-daemon=0a1b2c3d.result")));
        Assert.False(ElevatedHelperTrust.IsValidResultFile(Path.Combine(_dir, "elevated-start-daemon=0a1b2c3d.result")));
        Assert.False(ElevatedHelperTrust.IsValidResultFile(@"\\server\share\elevated-start-daemon=0a1b2c3d.result"));
    }

    [Theory]
    [InlineData(@"C:\PrivateNetRouting\sidecar", true)]
    [InlineData(@"C:\PrivateNetRouting\sidecar\nested", true)]
    [InlineData(@"sidecar", false)]
    [InlineData(@"\\server\share\sidecar", false)]
    [InlineData(@"\\.\pipe\sidecar", false)]
    [InlineData(@"C:\sidecar"" & echo pwned", false)]
    [InlineData(@"C:\sidecar%TEMP%", false)]
    [InlineData("", false)]
    public void AStateDirTheHelperCreatesMustBeAnInertAbsoluteLocalPath(string path, bool expected) =>
        Assert.Equal(expected, ElevatedHelperTrust.IsValidStateDir(path));

    [Fact]
    public async Task TheHelperRefusesAStateDirThatIsNotAnAbsoluteLocalPath()
    {
        var configPath = Path.Combine(_dir, "state.json");
        File.WriteAllText(configPath, """{ "chain": { "tailscale": { "stateDir": "relative\\sidecar" } } }""");
        var resultFile = Path.Combine(PinnedDir, "elevated-start-daemon=0a1b2c3d.result");

        var code = await Task.Run(() => ElevatedChainCommands.Execute(
            ElevatedChainCommands.StartDaemon, resultFile, configPath));

        Assert.Equal(1, code);
        var result = File.ReadAllText(resultFile);
        Assert.StartsWith("fail", result);
        Assert.Contains("stateDir", result);
    }

    [Fact]
    public async Task TheHelperLogIsPinnedToTheElevatedDirectory()
    {
        var configPath = Path.Combine(_dir, "foreign-logs.json");
        File.WriteAllText(configPath, """{ "logging": { "directory": "C:\\foreign\\logs" } }""");
        var resultFile = Path.Combine(PinnedDir, "elevated-unknown=0a1b2c3d.result");

        var code = await Task.Run(() => ElevatedChainCommands.Execute("--elevated-unknown", resultFile, configPath));

        Assert.Equal(0, code); // the unknown command itself is not the subject here
        Assert.StartsWith("ok", File.ReadAllText(resultFile));
        var pinnedLog = File.ReadAllText(Path.Combine(PinnedDir, "chain-elevated.log"));
        Assert.Contains("logging.directory", pinnedLog);
        Assert.Contains("ignored", pinnedLog);
        Assert.False(Directory.Exists(@"C:\foreign\logs"));
    }

    // ----- the elevated chain helper executes only the standard Tailscale install -----

    [Fact]
    public void ChainBinariesArePinnedToTheStandardInstall()
    {
        var tailscaled = Path.Combine(_dir, "tailscaled.exe");
        var cli = Path.Combine(_dir, "tailscale.exe");
        File.WriteAllText(tailscaled, "stub");
        File.WriteAllText(cli, "stub");
        var config = new AppConfig();
        config.Chain.Tailscale.TailscaledPath = @"C:\untrusted\tailscaled.exe";
        config.Chain.Tailscale.CliPath = @"C:\untrusted\tailscale.exe";
        using var log = new FileLog(Path.Combine(_dir, "pin.log"));

        ElevatedChainCommands.PinChainLocations(config, log, tailscaled, cli);

        Assert.Equal(tailscaled, config.Chain.Tailscale.TailscaledPath);
        Assert.Equal(cli, config.Chain.Tailscale.CliPath);
        var pinLog = File.ReadAllText(Path.Combine(_dir, "pin.log"));
        Assert.Contains("tailscaledPath", pinLog);
        Assert.Contains("ignored", pinLog);
        Assert.Contains("cliPath", pinLog);
    }

    [Fact]
    public void AChainHelperRunWithoutTheStandardInstallIsRefused()
    {
        var config = new AppConfig();
        using var log = new FileLog(Path.Combine(_dir, "pin-missing.log"));

        Assert.Throws<FileNotFoundException>(() =>
            ElevatedChainCommands.PinChainLocations(config, log, Path.Combine(_dir, "absent.exe"), Path.Combine(_dir, "also-absent.exe")));
    }

    [Fact]
    public void TheStopPinsTheCliWhenTheStandardInstallHasIt()
    {
        var cli = Path.Combine(_dir, "tailscale.exe");
        File.WriteAllText(cli, "stub");
        var config = new AppConfig();
        config.Chain.Tailscale.CliPath = @"C:\untrusted\tailscale.exe";
        using var log = new FileLog(Path.Combine(_dir, "pin-cli.log"));

        Assert.True(ElevatedChainCommands.TryPinCli(config, log, cli));
        Assert.Equal(cli, config.Chain.Tailscale.CliPath);
    }

    /// <summary>
    /// Without the standard install the stop is not refused: it skips its `tailscale down` step
    /// (the only binary it would execute) and still kills by image name and statedir/socket.
    /// </summary>
    [Fact]
    public void TheStopWithoutTheStandardInstallSkipsTheDownInsteadOfRefusing()
    {
        var config = new AppConfig();
        config.Chain.Tailscale.CliPath = @"C:\elsewhere\tailscale.exe";
        using var log = new FileLog(Path.Combine(_dir, "pin-cli-missing.log"));

        Assert.False(ElevatedChainCommands.TryPinCli(config, log, Path.Combine(_dir, "absent.exe")));
        Assert.Equal(@"C:\elsewhere\tailscale.exe", config.Chain.Tailscale.CliPath); // nothing is executed from it
    }

    /// <summary>The elevated start follows the pinned binary's own version: a 1.104.1+ daemon needs the dev flag even elevated.</summary>
    [Theory]
    [InlineData("1.104.1", true)]
    [InlineData("1.106.0", true)]
    [InlineData("1.104.0", false)]
    [InlineData("1.90.9", false)]
    [InlineData(null, false)]
    public void TheElevatedStartTakesTheDevFlagFromThePinnedBinarysVersion(string? version, bool devMode) =>
        Assert.Equal(devMode, ElevatedChainCommands.ElevatedDevMode(version is null ? null : new Version(version)));

    // ----- P2-22: the in-process path -----

    [Fact]
    public async Task InProcessPathReportsTheHelpersDetailAndCleansUpTheResult()
    {
        using var log = new FileLog(Path.Combine(_dir, "runner.log"));
        string? written = null;
        var runner = new ElevatedRunner(new ChainTailscaleConfig(), log, elevatedBypass: true, executor: (_, resultFile) =>
        {
            written = resultFile;
            ElevatedHelperTrust.WriteResultFile(resultFile, "fail the daemon refused to start");
            return 1;
        });

        var result = await runner.RunAsync("--elevated-test-command", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("the daemon refused to start", result.Detail);
        Assert.NotNull(written);
        Assert.False(File.Exists(written)); // no result files pile up
        Assert.False(File.Exists(written + ".tmp"));
        Assert.Contains("the daemon refused to start", File.ReadAllText(Path.Combine(_dir, "runner.log")));
    }

    [Fact]
    public async Task InProcessPathDoesNotBlockTheCaller()
    {
        using var log = new FileLog(Path.Combine(_dir, "runner.log"));
        using var release = new ManualResetEventSlim();
        var runner = new ElevatedRunner(new ChainTailscaleConfig(), log, elevatedBypass: true, executor: (_, resultFile) =>
        {
            // the command blocks (it polls with sleeps): run on the caller's thread it would freeze a UI
            release.Wait(TimeSpan.FromSeconds(5));
            ElevatedHelperTrust.WriteResultFile(resultFile, "ok done");
            return 0;
        });

        var run = runner.RunAsync("--elevated-test-command", CancellationToken.None);

        Assert.False(run.IsCompleted); // RunAsync handed the command off and returned
        release.Set();
        Assert.True((await run).Ok);
    }

    [Fact]
    public async Task StaleResultFilesAreSweptNewOnesAreKept()
    {
        Directory.CreateDirectory(PinnedDir);
        var stale = Path.Combine(PinnedDir, "elevated-old=00000000.result");
        var fresh = Path.Combine(PinnedDir, "elevated-new=11111111.result");
        File.WriteAllText(stale, "ok");
        File.WriteAllText(fresh, "ok");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        using var log = new FileLog(Path.Combine(_dir, "runner.log"));
        var runner = new ElevatedRunner(new ChainTailscaleConfig(), log, elevatedBypass: true, executor: (_, resultFile) =>
        {
            ElevatedHelperTrust.WriteResultFile(resultFile, "ok done");
            return 0;
        });

        await runner.RunAsync("--elevated-test-command", CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void AResultThatTheWriterStillHoldsIsNotAnError()
    {
        var path = Path.Combine(_dir, "elevated-x=22222222.result");
        File.WriteAllText(path, "ok done");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(ElevatedRunner.ReadResult(path)); // the wait loop goes on
        }

        Assert.Equal(new ElevatedResult(true, "done"), ElevatedRunner.ReadResult(path));
    }

    [Fact]
    public void AnUnfinishedResultIsNotAnAnswerYet()
    {
        var path = Path.Combine(_dir, "elevated-y=33333333.result");

        Assert.Null(ElevatedRunner.ReadResult(path));
        File.WriteAllText(path, string.Empty);
        Assert.Null(ElevatedRunner.ReadResult(path));
    }

    [Fact]
    public void ResultFilesAreWrittenAtomically()
    {
        var path = Path.Combine(_dir, "elevated-z=44444444.result");

        ElevatedHelperTrust.WriteResultFile(path, "ok first");
        ElevatedHelperTrust.WriteResultFile(path, "ok second");

        Assert.Equal("ok second", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ----- A-07: the elevated stop verifies that the daemon is really gone -----

    /// <summary>A stop of an already dead chain matches nothing and reports ok.</summary>
    [Fact]
    public async Task AHelperStopOfADeadChainReportsOk()
    {
        var socket = $@"\.\pipe\pnr-test-{Guid.NewGuid():N}";
        var resultFile = Path.Combine(PinnedDir, "elevated-stop-chain=0a1b2c3d.result");
        var code = await Task.Run(() => ElevatedChainCommands.Execute(
            ElevatedChainCommands.StopChain, resultFile, WriteTailscaleConfig(socket)));

        Assert.Equal(0, code);
        Assert.StartsWith("ok", File.ReadAllText(resultFile));
    }

    /// <summary>A daemon whose pipe still answers is a failed stop, never an "ok".</summary>
    [Fact]
    public async Task AHelperStopWithAStillRespondingDaemonReportsFail()
    {
        var name = $"pnr-test-{Guid.NewGuid():N}";
        var socket = $@"\.\pipe\{name}";
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var server = new NamedPipeServerStream(name, PipeDirection.In);
                    await server.WaitForConnectionAsync();
                }
            }
            catch (Exception)
            {
                // the test ended: the pump stops with it
            }
        });
        var resultFile = Path.Combine(PinnedDir, "elevated-stop-chain=0a1b2c3d.result");
        var code = await Task.Run(() => ElevatedChainCommands.Execute(
            ElevatedChainCommands.StopChain, resultFile, WriteTailscaleConfig(socket)));

        Assert.Equal(1, code);
        var result = File.ReadAllText(resultFile);
        Assert.StartsWith("fail", result);
        Assert.Contains("still there", result);
    }

    private string WriteTailscaleConfig(string socket)
    {
        var configPath = Path.Combine(_dir, $"chain-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, $$"""
            { "chain": { "tailscale": { "socket": "{{socket.Replace("\\", "\\\\")}}", "stateDir": "{{_dir.Replace("\\", "\\\\")}}\state" } } }
            """);
        return configPath;
    }
}
