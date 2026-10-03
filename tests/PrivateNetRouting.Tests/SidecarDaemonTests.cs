using System.IO.Pipes;
using PrivateNetRouting.Core.Chain;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Живость демона по отзывчивости именованного пайпа (WMI cmdline элевейтнутого процесса
/// не видна неэлевейтнутому читателю — Windows 11 24H2+).
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
