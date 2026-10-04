using System.Net;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Tests;

public class DohEndpointPoolTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-pool-{Guid.NewGuid():N}.log");
    private readonly List<FileLog> _logs = [];

    private static DohEndpoint Ep(int last) => new(IPAddress.Parse($"203.0.113.{last}"), 8480);

    private DohEndpointPool CreatePool(
        IReadOnlyList<DohEndpoint> seed,
        Func<DohEndpoint, CancellationToken, Task<bool>> prober,
        ManualTimeProvider time,
        IDohEndpointSource? source = null)
    {
        var log = new FileLog(_logPath);
        _logs.Add(log);
        return new DohEndpointPool(
            new Socks5Client("127.0.0.1:1"),
            seed,
            log,
            refreshInterval: TimeSpan.FromHours(1),
            probeTimeout: TimeSpan.FromSeconds(1),
            deadCooldown: TimeSpan.FromMinutes(5),
            probeName: "example.com",
            source: source,
            time: time,
            prober: prober);
    }

    [Fact]
    public async Task RefreshOrdersLiveFirst()
    {
        var time = new ManualTimeProvider();
        var a = Ep(1);
        var b = Ep(2);
        var c = Ep(3);
        using var pool = CreatePool([a, b, c], (ep, _) => Task.FromResult(ep != b), time);

        await pool.RefreshAsync(CancellationToken.None);
        await UntilAsync(() => pool.SnapshotAlive() is { Count: > 0 } alive && alive[^1].Equals(b));

        var alive = pool.SnapshotAlive();
        Assert.Equal(b, alive[^1]); // мёртвый уходит в конец
        Assert.Contains(a, alive);
        Assert.Contains(c, alive);
    }

    [Fact]
    public async Task DeadEndpointCoolsDownAndReturns()
    {
        var time = new ManualTimeProvider();
        var a = Ep(1);
        var b = Ep(2);
        using var pool = CreatePool([a, b], (_, _) => Task.FromResult(true), time);

        pool.MarkDead(a);
        Assert.Equal([b], pool.SnapshotAlive()); // остывающий исключён

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal([a, b], pool.SnapshotAlive()); // кулдаун истёк
    }

    [Fact]
    public async Task DiscoveryMergesSeed()
    {
        var time = new ManualTimeProvider();
        var seed = Ep(1);
        var discovered = Ep(9);
        var source = new StaticSource([discovered]);

        var probed = new List<DohEndpoint>();
        using var pool = CreatePool([seed], (ep, _) =>
        {
            probed.Add(ep);
            return Task.FromResult(true);
        }, time, source);

        await pool.RefreshAsync(CancellationToken.None);
        // фоновое обновление из конструктора гоняется с явным за общий gate — ждём результат
        await UntilAsync(() => probed.Count >= 2);

        Assert.Contains(seed, probed);
        Assert.Contains(discovered, probed);
    }

    [Fact]
    public async Task FailedDiscoveryKeepsSeed()
    {
        var time = new ManualTimeProvider();
        var seed = Ep(1);
        var source = new ThrowingSource();
        using var pool = CreatePool([seed], (_, _) => Task.FromResult(true), time, source);

        await pool.RefreshAsync(CancellationToken.None);

        Assert.Equal([seed], pool.SnapshotAlive());
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private sealed class StaticSource(IReadOnlyList<DohEndpoint> endpoints) : IDohEndpointSource
    {
        public Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct) =>
            Task.FromResult(endpoints);
    }

    private sealed class ThrowingSource : IDohEndpointSource
    {
        public Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct) =>
            throw new IOException("netmap недоступен");
    }

    public void Dispose()
    {
        foreach (var log in _logs)
        {
            log.Dispose();
        }

        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }
}
