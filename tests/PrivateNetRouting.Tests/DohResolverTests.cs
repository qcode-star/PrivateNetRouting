using System.Net;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Tests;

public class DohResolverTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-resolver-{Guid.NewGuid():N}.log");
    private readonly FileLog _log;

    public DohResolverTests()
    {
        _log = new FileLog(_logPath);
    }

    public void Dispose()
    {
        _log.Dispose();
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    private static DohEndpoint Ep(int last) => new(IPAddress.Parse($"203.0.113.{last}"), 8480);

    private DohEndpointPool Pool(params DohEndpoint[] endpoints) =>
        new(
            new Socks5Client("127.0.0.1:1"),
            endpoints,
            _log,
            refreshInterval: TimeSpan.FromHours(1),
            probeTimeout: TimeSpan.FromSeconds(1),
            deadCooldown: TimeSpan.FromMinutes(5),
            probeName: "example.com",
            prober: (_, _) => Task.FromResult(true));

    private DohResolver Resolver(DohEndpointPool pool, TimeSpan hedge, Func<DohEndpoint, string, CancellationToken, Task<DnsAnswer>> query) =>
        new(new Socks5Client("127.0.0.1:1"), pool, TimeSpan.FromSeconds(5), hedge, query);

    private static DnsAnswer Answer(params string[] ips) => new([.. ips.Select(IPAddress.Parse)], 120);

    [Fact]
    public async Task AnswersFromTheFirstEndpointWithoutStartingTheOthers()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var asked = new List<DohEndpoint>();
        var resolver = Resolver(pool, TimeSpan.FromMilliseconds(100), (endpoint, _, _) =>
        {
            lock (asked)
            {
                asked.Add(endpoint);
            }

            return Task.FromResult(Answer("198.51.100.1", "198.51.100.2", "198.51.100.3"));
        });

        var result = await resolver.ResolveAsync("name.example", CancellationToken.None);

        Assert.Equal(IPAddress.Parse("198.51.100.1"), result.Ip);
        Assert.Equal([IPAddress.Parse("198.51.100.2"), IPAddress.Parse("198.51.100.3")], result.Alternates);
        Assert.Equal([Ep(1)], asked);
    }

    /// <summary>A slow first endpoint must not hold the lookup for its whole timeout: the next one is raced against it.</summary>
    [Fact]
    public async Task ASlowEndpointIsRacedByTheNextOne()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var firstCancelled = new TaskCompletionSource();
        var resolver = Resolver(pool, TimeSpan.FromMilliseconds(100), async (endpoint, _, ct) =>
        {
            if (endpoint == Ep(1))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    firstCancelled.TrySetResult();
                    throw;
                }
            }

            return Answer("198.51.100.9");
        });

        var result = await resolver.ResolveAsync("name.example", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(IPAddress.Parse("198.51.100.9"), result.Ip);
        Assert.Contains("203.0.113.2", result.Source);
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the loser was cancelled, not left running
    }

    [Fact]
    public async Task AFailingEndpointFailsOverAtOnceAndIsMarkedDead()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var resolver = Resolver(pool, TimeSpan.FromMinutes(1), (endpoint, _, _) =>
            endpoint == Ep(1) ? throw new IOException("endpoint down") : Task.FromResult(Answer("198.51.100.9")));

        var result = await resolver.ResolveAsync("name.example", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(IPAddress.Parse("198.51.100.9"), result.Ip); // no waiting for the stagger
        Assert.Equal([Ep(2)], pool.SnapshotAlive());
    }

    /// <summary>A route-only (split-DNS) resolver denies general names: its NXDOMAIN must not end the lookup.</summary>
    [Fact]
    public async Task ANegativeAnswerFailsOverToTheNextEndpoint()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var resolver = Resolver(pool, TimeSpan.FromMinutes(1), (endpoint, _, _) =>
            endpoint == Ep(1) ? throw new DnsNegativeAnswerException("nxdomain") : Task.FromResult(Answer("198.51.100.9")));

        var result = await resolver.ResolveAsync("name.example", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(IPAddress.Parse("198.51.100.9"), result.Ip); // no waiting for the stagger
        Assert.Equal([Ep(1), Ep(2)], pool.SnapshotAlive()); // the denying endpoint is not put into cooldown
    }

    /// <summary>With the hedge running, a fast negative must not beat a slower positive.</summary>
    [Fact]
    public async Task AFastNegativeDoesNotBeatASlowerPositive()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var resolver = Resolver(pool, TimeSpan.FromMilliseconds(50), async (endpoint, _, ct) =>
        {
            if (endpoint == Ep(2))
            {
                throw new DnsNegativeAnswerException("nxdomain");
            }

            await Task.Delay(300, ct);
            return Answer("198.51.100.9");
        });

        var result = await resolver.ResolveAsync("name.example", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(IPAddress.Parse("198.51.100.9"), result.Ip);
        Assert.Contains("203.0.113.1", result.Source);
    }

    [Fact]
    public async Task EveryEndpointAnsweringNegativelyIsAFinalNegative()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var asked = 0;
        var resolver = Resolver(pool, TimeSpan.FromMinutes(1), (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            throw new DnsNegativeAnswerException("nxdomain");
        });

        await Assert.ThrowsAsync<DnsNegativeAnswerException>(() => resolver.ResolveAsync("typo.example", CancellationToken.None));

        Assert.Equal(2, asked);
        Assert.Equal([Ep(1), Ep(2)], pool.SnapshotAlive());
    }

    /// <summary>A failed endpoint might have known the name: negatives mixed with failures are not a final (cacheable) negative.</summary>
    [Fact]
    public async Task NegativesMixedWithFailuresAreNotAFinalNegative()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var resolver = Resolver(pool, TimeSpan.FromMinutes(1), (endpoint, _, _) =>
            endpoint == Ep(1) ? throw new IOException("endpoint down") : throw new DnsNegativeAnswerException("nxdomain"));

        var error = await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync("name.example", CancellationToken.None));

        Assert.Contains("all endpoints failed", error.Message);
    }

    /// <summary>A negative answer resolves nothing: it must not bring a cooling endpoint back.</summary>
    [Fact]
    public async Task ANegativeAnswerDoesNotPromoteTheEndpoint()
    {
        using var pool = Pool(Ep(1), Ep(2));
        await pool.RefreshAsync(CancellationToken.None); // a later refresh would clear the cooldowns
        pool.MarkDead(Ep(1));
        pool.MarkDead(Ep(2)); // all cooling: the snapshot hands out all of them
        var resolver = Resolver(pool, TimeSpan.FromMinutes(1), (endpoint, _, _) =>
            endpoint == Ep(1) ? throw new DnsNegativeAnswerException("nxdomain") : Task.FromResult(Answer("198.51.100.9")));

        await resolver.ResolveAsync("name.example", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([Ep(2)], pool.SnapshotAlive()); // only the answering one is alive again
    }

    /// <summary>An on-demand refresh can take the whole netmap timeout: the lookup goes on with the known endpoints after a short wait.</summary>
    [Fact]
    public async Task ALookupDoesNotWaitOutASlowPoolRefresh()
    {
        var source = new BlockingDohSource();
        using var pool = new DohEndpointPool(
            new Socks5Client("127.0.0.1:1"),
            [Ep(1)],
            _log,
            refreshInterval: TimeSpan.FromHours(1),
            probeTimeout: TimeSpan.FromSeconds(1),
            deadCooldown: TimeSpan.FromMinutes(5),
            probeName: "example.com",
            source: source,
            prober: (_, _) => Task.FromResult(true));
        var resolver = new DohResolver(
            new Socks5Client("127.0.0.1:1"),
            pool,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(1),
            (_, _, _) => Task.FromResult(Answer("198.51.100.9")),
            refreshWait: TimeSpan.FromMilliseconds(100));

        try
        {
            var result = await resolver.ResolveAsync("name.example", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(IPAddress.Parse("198.51.100.9"), result.Ip);
            Assert.False(pool.HasLive); // the refresh is still running in the background
        }
        finally
        {
            source.Release([Ep(1)]);
        }
    }

    [Fact]
    public async Task AllEndpointsFailingIsOneIoError()
    {
        using var pool = Pool(Ep(1), Ep(2));
        var resolver = Resolver(pool, TimeSpan.FromMilliseconds(50), (_, _, _) => throw new IOException("down"));

        var error = await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync("name.example", CancellationToken.None));

        Assert.Contains("all endpoints failed", error.Message);
    }

    [Fact]
    public async Task TheCallersCancellationAbortsTheLookup()
    {
        using var pool = Pool(Ep(1), Ep(2));
        using var cts = new CancellationTokenSource();
        var resolver = Resolver(pool, TimeSpan.FromMilliseconds(50), async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Answer("198.51.100.9");
        });

        var lookup = resolver.ResolveAsync("name.example", cts.Token);
        await Task.Delay(150);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal([Ep(1), Ep(2)], pool.SnapshotAlive()); // a cancelled lookup condemns nobody
    }

    [Fact]
    public async Task ConcurrentLookupsOfOneNameShareASingleResolution()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var inner = new FakeResolverAsync(async host =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return new ResolveResult(IPAddress.Parse("198.51.100.1"), "fake", 300);
        });
        var resolver = new CachingDnsResolver(inner, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(3600));

        var lookups = Enumerable.Range(0, 5).Select(_ => resolver.ResolveAsync("same.example", CancellationToken.None)).ToList();
        await Task.Delay(100);
        release.SetResult();
        var results = await Task.WhenAll(lookups);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(IPAddress.Parse("198.51.100.1"), r.Ip));
    }

    [Fact]
    public async Task ACancelledCallerDoesNotCancelTheSharedLookup()
    {
        var release = new TaskCompletionSource();
        var inner = new FakeResolverAsync(async host =>
        {
            await release.Task;
            return new ResolveResult(IPAddress.Parse("198.51.100.1"), "fake", 300);
        });
        var resolver = new CachingDnsResolver(inner, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(3600));
        using var cts = new CancellationTokenSource();

        var impatient = resolver.ResolveAsync("shared.example", cts.Token);
        var patient = resolver.ResolveAsync("shared.example", CancellationToken.None);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        release.SetResult();

        Assert.Equal(IPAddress.Parse("198.51.100.1"), (await patient).Ip);
    }

    [Fact]
    public async Task ManyNamesWithExpiryKeepResolving()
    {
        var time = new ManualTimeProvider();
        var inner = new FakeResolver(_ => new ResolveResult(IPAddress.Parse("198.51.100.1"), "fake", 60));
        var resolver = new CachingDnsResolver(inner, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), time);

        for (var i = 0; i < 5000; i++)
        {
            await resolver.ResolveAsync($"h{i}.example", CancellationToken.None);
            if (i % 1000 == 999 && i < 4999)
            {
                time.Advance(TimeSpan.FromSeconds(61)); // everything so far has expired
            }
        }

        Assert.Equal(5000, inner.Calls);
        await resolver.ResolveAsync("h4999.example", CancellationToken.None);
        Assert.Equal(5000, inner.Calls); // the newest entry is still cached
    }

    private sealed class FakeResolverAsync(Func<string, Task<ResolveResult>> resolve) : IDnsResolver
    {
        public Task<ResolveResult> ResolveAsync(string host, CancellationToken ct) => resolve(host);
    }
}
