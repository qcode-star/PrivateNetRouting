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

    private SidecarCli Create(string script, int cliTimeoutSeconds = 20, int loginWaitSeconds = 300)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "fake-tailscale.cmd");
        File.WriteAllText(path, script);
        return new SidecarCli(new ChainTailscaleConfig
        {
            CliPath = path,
            Socket = @"\\.\pipe\pnr-test",
            CliTimeoutSeconds = cliTimeoutSeconds,
            LoginWaitSeconds = loginWaitSeconds,
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

    [Fact]
    public async Task LoginParsesTheAuthUrlFromTheOutput()
    {
        var cli = Create(
            "@echo off\r\n" +
            "echo To authenticate, visit:\r\n" +
            "echo     https://login.tailscale.com/a/0123456789abcdef\r\n" +
            "exit /b 0\r\n");
        string? reported = null;

        var result = await cli.LoginAsync("https://control.example.net", url => reported = url, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("https://login.tailscale.com/a/0123456789abcdef", reported);
        Assert.Equal("https://login.tailscale.com/a/0123456789abcdef", result.AuthUrl);
    }

    [Fact]
    public async Task AHangingLoginFailsWithinTheLimit()
    {
        var cli = Create("@echo off\r\nping -n 30 127.0.0.1 > NUL\r\nexit /b 0\r\n", loginWaitSeconds: 1);
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            cli.LoginAsync(null, onAuthUrl: null, CancellationToken.None));

        stopwatch.Stop();
        Assert.Contains("login did not complete within", ex.Message);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"the login took {stopwatch.Elapsed}, the 1 s limit must hold");
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

/// <summary>The auth URL is extracted from the login output only after the "To authenticate" hint, once.</summary>
public class AuthUrlExtractorTests
{
    [Fact]
    public void UrlOnTheLineAfterTheHintIsExtracted()
    {
        var extractor = new AuthUrlExtractor();

        Assert.Null(extractor.Feed("To authenticate, visit:"));
        Assert.Equal("https://login.tailscale.com/a/0123456789abcdef", extractor.Feed("    https://login.tailscale.com/a/0123456789abcdef"));
    }

    [Fact]
    public void UrlOnTheSameLineAsTheHintIsExtracted()
    {
        var extractor = new AuthUrlExtractor();

        Assert.Equal("https://login.example/a/xyz", extractor.Feed("To authenticate, visit: https://login.example/a/xyz"));
    }

    [Fact]
    public void UrlsBeforeTheHintAreIgnored()
    {
        var extractor = new AuthUrlExtractor();

        Assert.Null(extractor.Feed("using control server https://control.example.net"));
        Assert.Null(extractor.Feed("post https://control.example.net: 200"));
        Assert.Equal("https://login.example/a/xyz", extractor.Feed("To authenticate, visit: https://login.example/a/xyz"));
    }

    [Fact]
    public void ItReportsOncePerHint()
    {
        var extractor = new AuthUrlExtractor();

        Assert.Null(extractor.Feed("To authenticate, visit:"));
        Assert.Equal("https://login.example/a/first", extractor.Feed("https://login.example/a/first"));
        // a stray URL after the extraction is ignored — a fresh hint must come first
        Assert.Null(extractor.Feed("https://login.example/a/second"));
        // a repeated hint (the first URL expired, tailscale printed a fresh one) is picked up
        Assert.Null(extractor.Feed("To authenticate, visit:"));
        Assert.Equal("https://login.example/a/third", extractor.Feed("https://login.example/a/third"));
    }

    [Fact]
    public void OrdinaryLinesYieldNothing()
    {
        var extractor = new AuthUrlExtractor();

        Assert.Null(extractor.Feed("log in to tailscale"));
        Assert.Null(extractor.Feed(""));
        Assert.Null(extractor.Feed("Success."));
    }
}

/// <summary>The control server is read out of the `tailscale debug prefs` JSON.</summary>
public class SystemControlUrlFinderTests
{
    [Fact]
    public void ControlUrlIsParsedFromThePrefsJson()
    {
        const string prefs = """
            {
              "ControlURL": "https://control.example.net",
              "RouteAll": false
            }
            """;

        Assert.Equal("https://control.example.net", SystemControlUrlFinder.ParseControlUrl(prefs));
    }

    [Fact]
    public void MissingControlUrlYieldsNull()
    {
        Assert.Null(SystemControlUrlFinder.ParseControlUrl("""{ "RouteAll": false }"""));
        Assert.Null(SystemControlUrlFinder.ParseControlUrl("failed to connect to the daemon"));
    }
}
