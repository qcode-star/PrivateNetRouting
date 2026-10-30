using System.IO.Pipes;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Daemon liveness via named-pipe responsiveness (WMI cmdline of an elevated process
/// is invisible to a non-elevated reader — Windows 11 24H2+).
/// </summary>
public class SidecarDaemonTests
{
    [Fact]
    public void PipeProbeDetectsLiveServer()
    {
        var name = $"pnr-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.In);
        var waiter = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            waiter.Set();
            server.WaitForConnection();
        });
        thread.Start();
        waiter.Wait();

        Assert.True(SidecarDaemon.ProbePipe($@"\\.\pipe\{name}", TimeSpan.FromSeconds(2)));
        thread.Join(2000);
    }

    [Fact]
    public void PipeProbeFailsOnMissingServer() =>
        Assert.False(SidecarDaemon.ProbePipe($@"\\.\pipe\pnr-test-absent-{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(200)));

    [Fact]
    public void PipeProbeRejectsNonPipePath() =>
        Assert.False(SidecarDaemon.ProbePipe(@"C:\somewhere\socket", TimeSpan.FromMilliseconds(200)));
}

/// <summary>The auto elevation rule: tailscale 1.104.1+ listens without elevation, older and unknown — not.</summary>
public class DaemonElevationDecisionTests
{
    [Theory]
    [InlineData("1.104.1", "1.104.1")]
    [InlineData("1.105.3", "1.105.3")]
    [InlineData("1.104.1-t7f4efe814-g8da26756c", "1.104.1")]
    [InlineData("  1.102.4  ", "1.102.4")]
    public void ParseVersionReadsTheHeadOfTheOutput(string line, string expected)
    {
        var parsed = TailscaleVersionProbe.ParseVersion(line);
        Assert.Equal(new Version(expected), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a version")]
    [InlineData("1.104")]
    public void ParseVersionRejectsUnparsableLines(string? line) =>
        Assert.Null(TailscaleVersionProbe.ParseVersion(line));

    [Theory]
    [InlineData(null, true)]
    [InlineData("1.102.4", true)]
    [InlineData("1.104.0", true)]
    [InlineData("1.104.1", false)]
    [InlineData("1.105.0", false)]
    public void AutoModeElevatesOnlyBelowTheFixVersion(string? version, bool expected) =>
        Assert.Equal(expected, SidecarDaemon.VersionNeedsElevation(version is null ? null : new Version(version)));

    [Fact]
    public void MinNonElevatedVersionIsTheSddlFixRelease() =>
        Assert.Equal(new Version(1, 104, 1), SidecarDaemon.MinNonElevatedVersion);

    [Theory]
    [InlineData("auto", DaemonElevationMode.Auto)]
    [InlineData("never", DaemonElevationMode.Never)]
    [InlineData("always", DaemonElevationMode.Always)]
    [InlineData(" ALWAYS ", DaemonElevationMode.Always)]
    [InlineData("bogus", DaemonElevationMode.Auto)]
    [InlineData(null, DaemonElevationMode.Auto)]
    public void ElevationSettingIsNormalized(string? text, DaemonElevationMode expected) =>
        Assert.Equal(expected, DaemonElevationParser.Parse(text));
}

/// <summary>
/// The daemon and keeper start through cmd.exe with values from the user-writable config: a value
/// that could close the argument and append a command must be refused before the line is built.
/// </summary>
public class TailscaleCommandLineTests
{
    private static ChainTailscaleConfig ValidConfig() => new()
    {
        TailscaledPath = @"C:\Program Files\Tailscale\tailscaled.exe",
        CliPath = @"C:\Program Files\Tailscale\tailscale.exe",
        StateDir = @"C:\ProgramData\pnr-test\sidecar",
        Socket = @"\\.\pipe\pnr-sidecar",
        SocksListen = "127.0.0.1:18083",
        HttpListen = "127.0.0.1:18082",
    };

    [Theory]
    [InlineData(@"\\.\pipe\pnr-sidecar", true)]
    [InlineData(@"\\.\pipe\pnr-sidecar.v2_a-b", true)]
    [InlineData(@"\\.\pipe\", false)]
    [InlineData(@"\\.\pipe\has space", false)]
    [InlineData(@"\\.\pipe\x"" & echo pwned", false)]
    [InlineData(@"C:\somewhere\socket", false)]
    [InlineData(@"\\server\share\socket", false)]
    public void SocketMustBeALocalNamedPipe(string socket, bool valid) =>
        Assert.Equal(valid, CommandLineSafety.IsValidPipePath(socket));

    [Fact]
    public void DaemonLineRefusesAValueThatWouldBreakOutOfItsArgument()
    {
        var config = ValidConfig();
        config.Socket = @"\\.\pipe\x"" & echo pwned";

        var error = Assert.Throws<InvalidDataException>(() => SidecarDaemon.BuildStartCommandLine(config, devMode: true));

        Assert.Contains("chain.tailscale.socket", error.Message);
    }

    [Fact]
    public void KeeperLineRefusesAValueThatWouldBreakOutOfItsArgument()
    {
        var config = ValidConfig();
        config.CliPath = @"C:\tools\"" & echo pwned\tailscale.exe";

        var error = Assert.Throws<InvalidDataException>(() => SidecarKeeper.BuildStartCommandLine(config));

        Assert.Contains("chain.tailscale.cliPath", error.Message);
    }

    [Fact]
    public void DaemonLineStillQuotesPathsWithSpaces()
    {
        var config = ValidConfig();
        config.StateDir = @"C:\Program Data\pnr sidecar";

        var line = SidecarDaemon.BuildStartCommandLine(config, devMode: true);

        Assert.Contains(@"--statedir=""C:\Program Data\pnr sidecar""", line);
        Assert.Contains(@"--socket=""\\.\pipe\pnr-sidecar""", line);
        Assert.DoesNotContain("\"\"", line);
    }

    [Fact]
    public void KeeperLineCarriesTheSocketAsOneQuotedArgument()
    {
        var line = SidecarKeeper.BuildStartCommandLine(ValidConfig());

        Assert.Contains(@"--socket=""\\.\pipe\pnr-sidecar""", line);
        Assert.EndsWith("debug watch-ipn", line);
    }
}

/// <summary>
/// A stop decides the daemon's death by its pipe, never by the port: a foreign process holding
/// the daemon port is a warning, not a stop failure.
/// </summary>
public class DaemonStopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pnr-daemon-{Guid.NewGuid():N}");

    public DaemonStopTests()
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

    private static ChainTailscaleConfig Config() => new()
    {
        TailscaledPath = @"C:\Program Files\Tailscale\tailscaled.exe",
        CliPath = @"C:\Program Files\Tailscale\tailscale.exe",
        StateDir = @"C:\ProgramData\pnr-test\sidecar",
        Socket = @"\.\pipe\pnr-test-sidecar",
        SocksListen = "127.0.0.1:18083",
        HttpListen = "127.0.0.1:18082",
        Elevation = "never",
    };

    [Fact]
    public async Task AForeignListenerOnTheDaemonPortIsAWarnNotAStopFailure()
    {
        var config = Config();
        var ports = new FakePortProbe { Listening = true }; // a foreign process on the daemon port
        using var log = new FileLog(Path.Combine(_dir, "daemon.log"));
        var daemon = new SidecarDaemon(
            config,
            ports,
            new FakeFinderByName(), // nothing of ours found, nothing killed
            log,
            new ElevatedRunner(config, log, elevatedBypass: true, executor: (_, _) => 0),
            new FakeVersionProbe());

        await daemon.KillAsync(CancellationToken.None); // must not throw

        Assert.Contains("foreign process", File.ReadAllText(Path.Combine(_dir, "daemon.log")));
    }

    [Fact]
    public async Task ARespondingPipeAfterTheKillFailsTheStop()
    {
        var name = $"pnr-test-{Guid.NewGuid():N}";
        var config = Config();
        config.Socket = $@"\.\pipe\{name}";
        using var server = new NamedPipeServerStream(name, PipeDirection.In);
        _ = Task.Run(server.WaitForConnection);
        var ports = new FakePortProbe();
        using var log = new FileLog(Path.Combine(_dir, "daemon.log"));
        var daemon = new SidecarDaemon(
            config,
            ports,
            new FakeFinderByName(),
            log,
            new ElevatedRunner(config, log, elevatedBypass: true, executor: (_, _) => 0),
            new FakeVersionProbe());

        await Assert.ThrowsAsync<IOException>(() => daemon.KillAsync(CancellationToken.None));
    }

    private sealed class FakeVersionProbe : ITailscaleVersionProbe
    {
        public Version? GetVersion() => new(1, 104, 1);
    }
}
