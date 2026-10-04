using System.Net;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Runtime;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

public class RuntimePiecesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-runtime-").FullName;

    [Fact]
    public void CmdArgumentsWrapQuotedCommandLineOnce()
    {
        // каноническая форма ""cmd" args 1>out 2>err"": cmd срывает первую и последнюю
        // кавычки; двойная оборачивка ломала пути с пробелами ("""C:\Program …")
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

        Assert.Equal(1, inner.Calls); // 30с < зажатого TTL 60с — из кэша
        Assert.Equal(first.Ip, second.Ip);

        time.Advance(TimeSpan.FromSeconds(31)); // суммарно 61с > 60с
        await resolver.ResolveAsync("short.example", CancellationToken.None);
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
        time.Advance(TimeSpan.FromSeconds(3601)); // чуть больше максимума
        await resolver.ResolveAsync("long.example", CancellationToken.None);

        Assert.Equal(2, inner.Calls); // долгий TTL тоже зажат
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
        Assert.Equal(first, File.GetLastWriteTimeUtc(markerPath)); // троттлинг

        time.Advance(TimeSpan.FromSeconds(51));
        marker.Touch();
        Assert.True(File.GetLastWriteTimeUtc(markerPath) > first);
    }

    [Fact]
    public void FileLogRotatesAtStartup()
    {
        var logPath = Path.Combine(_tempDir, "app.log");
        File.WriteAllBytes(logPath, new byte[2_000_000]); // больше максимума

        using (var log = new FileLog(logPath, maxBytes: 1_000_000, keep: 2))
        {
            log.Info("после ротации");
        }

        Assert.True(File.Exists($"{logPath}.1"));
        Assert.Contains("после ротации", File.ReadAllText(logPath));
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
    [InlineData("nonsense", LogLevel.Info)] // незнакомое значение — дефолт
    public void LogLevelParsesWithInfoFallback(string? raw, LogLevel expected)
    {
        Assert.Equal(expected, LogLevelParser.Parse(raw));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
