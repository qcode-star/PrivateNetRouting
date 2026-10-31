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
        Assert.Equal(b, alive[^1]); // the dead one moves to the end
        Assert.Contains(a, alive);
        Assert.Contains(c, alive);
    }

    [Fact]
    public void NonPositiveIntervalsAreFlooredToOneSecond()
    {
        var log = new FileLog(_logPath);
        _logs.Add(log);
        using var pool = new DohEndpointPool(
            new Socks5Client("127.0.0.1:1"),
            [],
            log,
            refreshInterval: TimeSpan.Zero,
            probeTimeout: TimeSpan.FromSeconds(-5),
            deadCooldown: TimeSpan.Zero,
            probeName: "example.com",
            time: new ManualTimeProvider());

        Assert.Equal(TimeSpan.FromSeconds(1), pool.NextRefreshDelay()); // a zero would spin the loop
    }

    [Fact]
    public async Task DeadEndpointCoolsDownAndReturns()
    {
        var time = new ManualTimeProvider();
        var a = Ep(1);
        var b = Ep(2);
        using var pool = CreatePool([a, b], (_, _) => Task.FromResult(true), time);

        pool.MarkDead(a);
        Assert.Equal([b], pool.SnapshotAlive()); // the cooling one is excluded

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal([a, b], pool.SnapshotAlive()); // cooldown has expired
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
        // the background refresh from the constructor races the explicit one for the shared gate, so wait for the result
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

    /// <summary>The refresh is periodic: a discovery failure that repeats verbatim is one Warn, not one per round.</summary>
    [Fact]
    public async Task RepeatedDiscoveryFailureIsWarnedOnce()
    {
        var time = new ManualTimeProvider();
        using var pool = CreatePool([Ep(1)], (_, _) => Task.FromResult(true), time, new ThrowingSource());

        await pool.RefreshAsync(CancellationToken.None);
        await pool.RefreshAsync(CancellationToken.None);
        await pool.RefreshAsync(CancellationToken.None);

        var text = File.ReadAllText(_logPath);
        Assert.Equal(1, text.Split("discovery failed").Length - 1);
    }

    [Fact]
    public async Task EmptyPoolRefreshesOnDemandOnceTheChainIsUp()
    {
        var time = new ManualTimeProvider();
        var source = new SwitchableSource();
        using var pool = CreatePool([], (_, _) => Task.FromResult(true), time, source);

        // built before the chain: discovery fails, nothing is live
        await pool.RefreshAsync(CancellationToken.None);
        Assert.False(pool.HasLive);
        Assert.Empty(pool.SnapshotAlive());
        var callsWhileDown = source.Calls;

        // the chain comes up; a second ask right away is throttled …
        source.Endpoints = [Ep(9)];
        await pool.EnsureLiveAsync(CancellationToken.None);
        Assert.Equal(callsWhileDown, source.Calls);
        Assert.False(pool.HasLive);

        // … and after the minimum gap the pool refreshes itself on demand
        time.Advance(TimeSpan.FromSeconds(10));
        await pool.EnsureLiveAsync(CancellationToken.None);
        Assert.True(pool.HasLive);
        Assert.Equal([Ep(9)], pool.SnapshotAlive());

        // a live pool is left alone
        var callsWhenLive = source.Calls;
        time.Advance(TimeSpan.FromMinutes(1));
        await pool.EnsureLiveAsync(CancellationToken.None);
        Assert.Equal(callsWhenLive, source.Calls);
    }

    /// <summary>Over a down chain every round spawns discovery and probes: the empty-pool retry backs off and resets on success.</summary>
    [Fact]
    public async Task EmptyPoolRetryBacksOffAndResetsOnceLive()
    {
        var time = new ManualTimeProvider();
        var source = new SwitchableSource();
        using var pool = CreatePool([], (_, _) => Task.FromResult(true), time, source);

        await pool.RefreshAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(15), pool.NextRefreshDelay());
        await pool.RefreshAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(30), pool.NextRefreshDelay());
        await pool.RefreshAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(60), pool.NextRefreshDelay());

        for (var i = 0; i < 10; i++)
        {
            await pool.RefreshAsync(CancellationToken.None);
        }

        Assert.Equal(TimeSpan.FromHours(1), pool.NextRefreshDelay()); // capped by the refresh interval

        source.Endpoints = [Ep(9)];
        await pool.RefreshAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(1), pool.NextRefreshDelay());

        // the chain goes down again and the probes fail: the retry starts over from the short step
        source.Endpoints = null;
        using var dying = CreatePool([Ep(1)], (_, _) => Task.FromResult(false), time, source);
        await dying.RefreshAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(15), dying.NextRefreshDelay());
    }

    /// <summary>A pool that keeps coming back empty is not refreshed on demand every few seconds of lookups.</summary>
    [Fact]
    public async Task OnDemandRefreshGapBacksOffWhileThePoolStaysEmpty()
    {
        var time = new ManualTimeProvider();
        var source = new SwitchableSource();
        using var pool = CreatePool([], (_, _) => Task.FromResult(true), time, source);

        await pool.RefreshAsync(CancellationToken.None);
        await pool.RefreshAsync(CancellationToken.None);
        await pool.RefreshAsync(CancellationToken.None);
        var calls = source.Calls;

        source.Endpoints = [Ep(9)];
        time.Advance(TimeSpan.FromSeconds(10)); // past the first gap, not past the backed-off one
        await pool.EnsureLiveAsync(CancellationToken.None);
        Assert.Equal(calls, source.Calls);

        time.Advance(TimeSpan.FromSeconds(6)); // the gap never grows past the empty-pool retry step
        await pool.EnsureLiveAsync(CancellationToken.None);
        Assert.True(pool.HasLive);
    }

    /// <summary>Every `pnr` command composes a runtime: a pool nobody asks must not spawn discovery or probe anything.</summary>
    [Fact]
    public async Task AnUnusedPoolDoesNoBackgroundWork()
    {
        var time = new ManualTimeProvider();
        var source = new SwitchableSource { Endpoints = [Ep(9)] };
        var probes = 0;
        using var pool = CreatePool([Ep(1)], (_, _) =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(true);
        }, time, source);

        await Task.Delay(300);

        Assert.Equal(0, source.Calls);
        Assert.Equal(0, probes);
        Assert.Equal([Ep(1)], pool.SnapshotAlive()); // the seed is usable as it stands

        // the first ask starts the work
        await pool.EnsureLiveAsync(CancellationToken.None);
        Assert.Equal(1, source.Calls);
        Assert.True(pool.HasLive);
    }

    [Fact]
    public async Task FailedDiscoveryKeepsPreviouslyDiscoveredEndpoints()
    {
        var time = new ManualTimeProvider();
        var seed = Ep(1);
        var found = Ep(9);
        var source = new SwitchableSource { Endpoints = [found] };
        using var pool = CreatePool([seed], (_, _) => Task.FromResult(true), time, source);

        await pool.RefreshAsync(CancellationToken.None);
        Assert.Contains(found, pool.SnapshotAlive());

        // the chain went down: discovery fails, the discovered endpoint must survive the refresh
        source.Endpoints = null;
        await pool.RefreshAsync(CancellationToken.None);
        Assert.Contains(found, pool.SnapshotAlive());
        Assert.Contains(seed, pool.SnapshotAlive());
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

    /// <summary>A discovery source that fails (null endpoints — the sidecar is down) or answers, switchable mid-test.</summary>
    private sealed class SwitchableSource : IDohEndpointSource
    {
        private int _calls;

        public IReadOnlyList<DohEndpoint>? Endpoints { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Endpoints is { } endpoints
                ? Task.FromResult(endpoints)
                : throw new IOException("netmap unavailable");
        }
    }

    private sealed class ThrowingSource : IDohEndpointSource
    {
        public Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct) =>
            throw new IOException("netmap unavailable");
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
