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

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
