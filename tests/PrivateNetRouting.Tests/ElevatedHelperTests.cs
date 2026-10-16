using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>
/// How "our" sidecar processes are recognized (a whole argument value, not a substring), the
/// command line of the elevated helper (the config of the asking user travels with it) and the
/// result-file round trip of the in-process path.
/// </summary>
public class ElevatedHelperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pnr-helper-{Guid.NewGuid():N}");

    public ElevatedHelperTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Roaming\PrivateNetRouting\appsettings.json", true)]
    [InlineData(@"relative\appsettings.json", false)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", false)]
    [InlineData("", false)]
    public void OnlyAnAbsoluteJsonFileIsAcceptedAsTheHelperConfig(string path, bool expected) =>
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
        var resultFile = Path.Combine(_dir, "elevated-start-daemon=0a1b2c3d.result");

        var code = await Task.Run(() => ElevatedChainCommands.Execute(
            ElevatedChainCommands.StartDaemon, resultFile, configPath: @"relative\not-absolute.json"));

        Assert.Equal(1, code);
        Assert.StartsWith("fail", File.ReadAllText(resultFile));
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

    // ----- P2-22: the in-process path -----

    [Fact]
    public async Task InProcessPathReportsTheHelpersDetailAndCleansUpTheResult()
    {
        var stateDir = Path.Combine(_dir, "state");
        using var log = new FileLog(Path.Combine(_dir, "runner.log"));
        string? written = null;
        var runner = new ElevatedRunner(new ChainTailscaleConfig { StateDir = stateDir }, log, elevatedBypass: true, executor: (_, resultFile) =>
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
        var stateDir = Path.Combine(_dir, "state");
        using var log = new FileLog(Path.Combine(_dir, "runner.log"));
        using var release = new ManualResetEventSlim();
        var runner = new ElevatedRunner(new ChainTailscaleConfig { StateDir = stateDir }, log, elevatedBypass: true, executor: (_, resultFile) =>
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
        var stateDir = Path.Combine(_dir, "state");
        Directory.CreateDirectory(stateDir);
        var stale = Path.Combine(stateDir, "elevated-old=00000000.result");
        var fresh = Path.Combine(stateDir, "elevated-new=11111111.result");
        File.WriteAllText(stale, "ok");
        File.WriteAllText(fresh, "ok");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        using var log = new FileLog(Path.Combine(_dir, "runner.log"));
        var runner = new ElevatedRunner(new ChainTailscaleConfig { StateDir = stateDir }, log, elevatedBypass: true, executor: (_, resultFile) =>
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
}
