using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Tests;

/// <summary>
/// SidecarCli against a real child process (a batch file stands in for tailscale.exe): the
/// output comes back on the happy path, and a call that hangs past chain.cliTimeoutSeconds
/// fails with a clear error while the process is killed — a daemon that stopped answering its
/// pipe must not hang the raise/stop/poll that called it.
/// </summary>
public class SidecarCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pnr-cli-{Guid.NewGuid():N}");

    private SidecarCli Create(string script, int cliTimeoutSeconds = 20)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "fake-tailscale.cmd");
        File.WriteAllText(path, script);
        return new SidecarCli(new ChainConfig
        {
            TailscaleCliPath = path,
            Socket = @"\\.\pipe\pnr-test",
            CliTimeoutSeconds = cliTimeoutSeconds,
        });
    }

    [Fact]
    public async Task HappyPathReturnsOutputAndExitCode()
    {
        var cli = Create("@echo off\r\necho status-reply\r\nexit /b 0\r\n");

        var result = await cli.RunAsync(["status"], CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("status-reply", result.Output);
    }

    [Fact]
    public async Task AHangingCallFailsWithinTheLimit()
    {
        var cli = Create("@echo off\r\nping -n 30 127.0.0.1 > NUL\r\nexit /b 0\r\n", cliTimeoutSeconds: 1);
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            cli.RunAsync(["status"], CancellationToken.None));

        stopwatch.Stop();
        Assert.Contains("did not exit within", ex.Message);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"the call took {stopwatch.Elapsed}, the 1 s limit must hold");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // the killed batch's console may release the file a moment later
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
