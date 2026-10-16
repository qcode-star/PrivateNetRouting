using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Resolution via a pool of DoH endpoints over a SOCKS5 upstream. Candidates are tried in pool
/// order, but a slow one does not hold the lookup for its whole timeout: after a short stagger
/// the next candidate is started in parallel and the first answer wins (the losers are
/// cancelled). A failing endpoint is marked dead (cooldown in the pool). A negative answer
/// (NXDOMAIN/NODATA) does not end the lookup while candidates remain: a route-only (split-DNS)
/// resolver of the tailnet answers NXDOMAIN for every general name, so the lookup fails over
/// (a running hedge is waited for) and only a lookup where every tried endpoint answered
/// negatively is a final negative. A negative answer neither marks the endpoint dead nor alive.
/// </summary>
public sealed class DohResolver : IDnsResolver
{
    /// <summary>How long a candidate gets alone before the next one is started alongside it.</summary>
    public static readonly TimeSpan DefaultHedgeDelay = TimeSpan.FromSeconds(2);

    /// <summary>How many addresses beyond the first one an answer hands on as alternates.</summary>
    private const int MaxAlternates = 3;

    private readonly DohEndpointPool _pool;
    private readonly TimeSpan _hedgeDelay;
    private readonly TimeSpan _refreshWait;
    private readonly Func<DohEndpoint, string, CancellationToken, Task<DnsAnswer>> _query;

    public DohResolver(
        Socks5Client socks,
        DohEndpointPool pool,
        TimeSpan timeout,
        TimeSpan? hedgeDelay = null,
        Func<DohEndpoint, string, CancellationToken, Task<DnsAnswer>>? query = null,
        TimeSpan? refreshWait = null)
    {
        _pool = pool;
        _hedgeDelay = hedgeDelay ?? DefaultHedgeDelay;
        // a lookup waits for an on-demand pool refresh no longer than one DoH query may take
        _refreshWait = refreshWait ?? timeout;
        _query = query ?? ((endpoint, host, ct) => DohClient.QueryAsync(socks, endpoint, host, timeout, ct));
    }

    private sealed record Attempt(DohEndpoint Endpoint, DnsAnswer? Answer, Exception? Error);

    public async Task<ResolveResult> ResolveAsync(string host, CancellationToken ct)
    {
        // a pool built before the chain came up (or refreshed over a down chain) holds nothing
        // answering: refresh it now instead of waiting for the next timer tick — but a refresh
        // can take the whole netmap timeout plus the probes, so the lookup waits only a bounded
        // time and then goes on with what the pool already knows (the refresh keeps running)
        using (var refreshBound = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            refreshBound.CancelAfter(_refreshWait);
            try
            {
                await _pool.EnsureLiveAsync(refreshBound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }

        var candidates = _pool.SnapshotAlive();
        if (candidates.Count == 0)
        {
            throw new IOException($"doh: no endpoint known yet for {host} (the pool is still being refreshed)");
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<Task<Attempt>>();
        var next = 0;
        Exception? lastError = null;
        DnsNegativeAnswerException? negative = null;
        var failures = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (running.Count == 0 && next < candidates.Count)
                {
                    running.Add(AttemptAsync(candidates[next++], host, race.Token));
                }

                if (running.Count == 0)
                {
                    break;
                }

                using var staggerCts = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
                var stagger = next < candidates.Count ? Task.Delay(_hedgeDelay, staggerCts.Token) : null;
                var waitFor = new List<Task>(running);
                if (stagger is not null)
                {
                    waitFor.Add(stagger);
                }

                var finished = await Task.WhenAny(waitFor).ConfigureAwait(false);
                if (finished == stagger)
                {
                    ct.ThrowIfCancellationRequested();
                    running.Add(AttemptAsync(candidates[next++], host, race.Token));
                    continue;
                }

                await staggerCts.CancelAsync().ConfigureAwait(false);
                var done = (Task<Attempt>)finished;
                running.Remove(done);
                var attempt = await done.ConfigureAwait(false);
                if (attempt.Answer is { } answer)
                {
                    _pool.MarkAlive(attempt.Endpoint);
                    return new ResolveResult(
                        answer.Ips[0], attempt.Endpoint.ToString(), answer.MinTtl, [.. answer.Ips.Skip(1).Take(MaxAlternates)]);
                }

                ct.ThrowIfCancellationRequested();
                if (attempt.Error is DnsNegativeAnswerException miss)
                {
                    // the endpoint answered, but it may be a route-only resolver that knows only
                    // its own zones: the others still get their say, and the endpoint is neither
                    // promoted (it did not resolve anything) nor put into cooldown (it works)
                    negative = miss;
                    continue;
                }

                // any endpoint failure (including a bad certificate) fails over to the next one;
                // only the caller's own cancellation aborts the lookup
                _pool.MarkDead(attempt.Endpoint);
                lastError = attempt.Error;
                failures++;
            }
        }
        finally
        {
            await race.CancelAsync().ConfigureAwait(false); // the losers of the race stop here
        }

        // a final negative (cacheable) only when every tried endpoint said so; a mix with failed
        // endpoints is not one — a failed endpoint might have known the name
        if (negative is not null && failures == 0)
        {
            throw negative;
        }

        var reason = negative is null ? lastError?.Message : $"{lastError?.Message}; others: {negative.Message}";
        throw new IOException($"doh: all endpoints failed for {host}: {reason}", lastError);
    }

    /// <summary>One query as a value: a failure is data (it must not fault a task nobody awaits any more).</summary>
    private async Task<Attempt> AttemptAsync(DohEndpoint endpoint, string host, CancellationToken ct)
    {
        try
        {
            return new Attempt(endpoint, await _query(endpoint, host, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return new Attempt(endpoint, null, ex);
        }
    }
}

/// <summary>
/// Cache on top of the resolver: the response TTL is clamped to [min, max] — short answers from
/// corporate resolvers do not force hitting DoH on every request, long ones do not hide an address
/// change. Concurrent lookups of one name share a single inner resolution (single-flight), a
/// negative answer is remembered briefly, expired entries are dropped when met and when the
/// cache grows.
/// </summary>
public sealed class CachingDnsResolver(IDnsResolver inner, TimeSpan ttlMin, TimeSpan ttlMax, TimeProvider? time = null)
    : IDnsResolver
{
    private const int MaxEntries = 4096;
    private const int MaxNegativeEntries = 1024;
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ResolveResult Result, DateTimeOffset Expires)> _cache = [];
    private readonly Dictionary<string, (DnsNegativeAnswerException Error, DateTimeOffset Expires)> _negative = [];
    private readonly Dictionary<string, Task<ResolveResult>> _inflight = [];

    public async Task<ResolveResult> ResolveAsync(string host, CancellationToken ct)
    {
        Task<ResolveResult> flight;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_cache.TryGetValue(host, out var hit))
            {
                if (hit.Expires > now)
                {
                    return hit.Result;
                }

                _cache.Remove(host);
            }

            if (_negative.TryGetValue(host, out var miss))
            {
                if (miss.Expires > now)
                {
                    throw new DnsNegativeAnswerException(miss.Error.Message);
                }

                _negative.Remove(host);
            }

            if (!_inflight.TryGetValue(host, out flight!))
            {
                // the shared lookup is not tied to the first caller's token: the others wait on it
                // too (the inner resolver bounds itself with its own timeouts)
                flight = Task.Run(() => ResolveAndRememberAsync(host), CancellationToken.None);
                _inflight[host] = flight;
            }
        }

        return await flight.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ResolveResult> ResolveAndRememberAsync(string host)
    {
        try
        {
            var resolved = await inner.ResolveAsync(host, CancellationToken.None).ConfigureAwait(false);
            var ttl = resolved.TtlSeconds is { } seconds
                ? TimeSpan.FromSeconds(Math.Clamp(seconds, (long)ttlMin.TotalSeconds, (long)ttlMax.TotalSeconds))
                : ttlMin;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                MakeRoom(_cache, MaxEntries, now);
                _cache[host] = (resolved, now + ttl);
            }

            return resolved;
        }
        catch (DnsNegativeAnswerException ex)
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                MakeRoom(_negative, MaxNegativeEntries, now);
                _negative[host] = (ex, now + NegativeTtl);
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _inflight.Remove(host);
            }
        }
    }

    /// <summary>Before an insert: drop what has expired, and everything if that was not enough.</summary>
    private static void MakeRoom<TValue>(Dictionary<string, (TValue Value, DateTimeOffset Expires)> entries, int limit, DateTimeOffset now)
    {
        if (entries.Count < limit)
        {
            return;
        }

        foreach (var key in entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
        {
            entries.Remove(key);
        }

        if (entries.Count >= limit)
        {
            entries.Clear();
        }
    }
}
