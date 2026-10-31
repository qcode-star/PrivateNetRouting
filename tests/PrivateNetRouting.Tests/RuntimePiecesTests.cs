using System.Net;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Runtime;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

public class RuntimePiecesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-runtime-").FullName;

    /// <summary>A trailing separator would escape the closing quote of the explorer argument (both "Open logs" buttons share it).</summary>
    [Fact]
    public void ExplorerFolderArgumentCarriesNoTrailingSeparator()
    {
        Assert.Equal("\"C:\\logs\"", ProcessOps.ExplorerFolderArgument(@"C:\logs\"));
        Assert.Equal("\"C:\\logs\"", ProcessOps.ExplorerFolderArgument(@"C:\logs"));
    }

    [Fact]
    public void CmdArgumentsWrapQuotedCommandLineOnce()
    {
        // canonical form ""cmd" args 1>out 2>err"": cmd strips the first and last
        // quotes; double wrapping broke paths with spaces ("""C:\Program …")
        var arguments = ProcessOps.BuildCmdArguments(
            "\"C:\\Program Files\\Tailscale\\tailscaled.exe\" --port=0",
            @"C:\logs\out.log",
            @"C:\logs\err.log");

        Assert.Equal(
            "/c \"\"C:\\Program Files\\Tailscale\\tailscaled.exe\" --port=0 1>\"C:\\logs\\out.log\" 2>\"C:\\logs\\err.log\"\"",
            arguments);
    }

    [Fact]
    public void CmdArgumentsWithoutStdoutRedirectToNul()
    {
        var arguments = ProcessOps.BuildCmdArguments("cmd /c exit", null, @"C:\logs\err.log");

        Assert.Equal("/c \"cmd /c exit 1>NUL 2>\"C:\\logs\\err.log\"\"", arguments);
    }

    [Fact]
    public async Task CacheClampsShortTtlAndHits()
    {
        var time = new ManualTimeProvider();
        var inner = new FakeResolver(_ => new ResolveResult(IPAddress.Parse("203.0.113.7"), "fake", TtlSeconds: 5));
        var resolver = new CachingDnsResolver(
            inner, ttlMin: TimeSpan.FromSeconds(60), ttlMax: TimeSpan.FromSeconds(3600), time: time);

        var first = await resolver.ResolveAsync("short.example", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));
        var second = await resolver.ResolveAsync("short.example", CancellationToken.None);

        Assert.Equal(1, inner.Calls); // 30s < the clamped TTL of 60s, so from cache
        Assert.Equal(first.Ip, second.Ip);

        time.Advance(TimeSpan.FromSeconds(31)); // 61s total > 60s
        await resolver.ResolveAsync("short.example", CancellationToken.None);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task NegativeAnswerIsCachedBrieflyThenAsksAgain()
    {
        var time = new ManualTimeProvider();
        var inner = new FakeResolver(_ => throw new DnsNegativeAnswerException("nxdomain"));
        var resolver = new CachingDnsResolver(
            inner, ttlMin: TimeSpan.FromSeconds(60), ttlMax: TimeSpan.FromSeconds(3600), time: time);

        await Assert.ThrowsAsync<DnsNegativeAnswerException>(() => resolver.ResolveAsync("typo.example", CancellationToken.None));
        await Assert.ThrowsAsync<DnsNegativeAnswerException>(() => resolver.ResolveAsync("typo.example", CancellationToken.None));
        Assert.Equal(1, inner.Calls); // the second miss comes from the negative cache

        time.Advance(TimeSpan.FromSeconds(11));
        await Assert.ThrowsAsync<DnsNegativeAnswerException>(() => resolver.ResolveAsync("typo.example", CancellationToken.None));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CacheClampsLongTtl()
    {
        var time = new ManualTimeProvider();
        var inner = new FakeResolver(_ => new ResolveResult(IPAddress.Parse("203.0.113.8"), "fake", TtlSeconds: 86_400));
        var resolver = new CachingDnsResolver(
            inner, ttlMin: TimeSpan.FromSeconds(60), ttlMax: TimeSpan.FromSeconds(3600), time: time);

        await resolver.ResolveAsync("long.example", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(3601)); // just over the maximum
        await resolver.ResolveAsync("long.example", CancellationToken.None);

        Assert.Equal(2, inner.Calls); // the long TTL is clamped too
    }

    [Fact]
    public void ActivityMarkerIsThrottled()
    {
        var time = new ManualTimeProvider();
        var markerPath = Path.Combine(_tempDir, "marker");
        var marker = new ActivityMarker(markerPath, TimeSpan.FromSeconds(60), time);

        marker.Touch();
        Assert.True(File.Exists(markerPath));
        var first = File.GetLastWriteTimeUtc(markerPath);

        time.Advance(TimeSpan.FromSeconds(10));
        marker.Touch();
        Assert.Equal(first, File.GetLastWriteTimeUtc(markerPath)); // throttling

        time.Advance(TimeSpan.FromSeconds(51));
        marker.Touch();
        Assert.True(File.GetLastWriteTimeUtc(markerPath) > first);
    }

    [Fact]
    public void FileLogRotatesAtStartup()
    {
        var logPath = Path.Combine(_tempDir, "app.log");
        File.WriteAllBytes(logPath, new byte[2_000_000]); // larger than the maximum

        using (var log = new FileLog(logPath, maxBytes: 1_000_000, keep: 2))
        {
            log.Info("after rotation");
        }

        Assert.True(File.Exists($"{logPath}.1"));
        Assert.Contains("after rotation", File.ReadAllText(logPath));
    }

    [Fact]
    public void FileLogFiltersRecordsBelowLevel()
    {
        var logPath = Path.Combine(_tempDir, "levels.log");
        using (var log = new FileLog(logPath, LogLevel.Warn))
        {
            log.Debug("debug line");
            log.Info("info line");
            log.Warn("warn line");
            log.Error("error line");
        }

        var text = File.ReadAllText(logPath);
        Assert.DoesNotContain("debug line", text);
        Assert.DoesNotContain("info line", text);
        Assert.Contains("WARN warn line", text);
        Assert.Contains("ERROR error line", text);
    }

    [Fact]
    public void FileLogDefaultsToInfo()
    {
        var logPath = Path.Combine(_tempDir, "default-level.log");
        using (var log = new FileLog(logPath))
        {
            log.Debug("debug line");
            log.Info("info line");
        }

        var text = File.ReadAllText(logPath);
        Assert.DoesNotContain("debug line", text);
        Assert.Contains("INFO info line", text);
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("", LogLevel.Info)]
    [InlineData("warning", LogLevel.Warn)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("nonsense", LogLevel.Info)] // unknown value falls back to the default
    public void LogLevelParsesWithInfoFallback(string? raw, LogLevel expected)
    {
        Assert.Equal(expected, LogLevelParser.Parse(raw));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
