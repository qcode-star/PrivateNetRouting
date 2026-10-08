using System.IO.Pipes;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;

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
