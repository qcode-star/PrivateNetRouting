using System.Net.Sockets;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Resolution via a pool of DoH endpoints over a SOCKS5 upstream. Iterates over candidates,
/// marks a failing one dead (cooldown in the pool); the first successful answer is taken.
/// </summary>
public sealed class DohResolver(Socks5Client socks, DohEndpointPool pool, TimeSpan timeout) : IDnsResolver
{
    public async Task<ResolveResult> ResolveAsync(string host, CancellationToken ct)
    {
        // a pool built before the chain came up (or refreshed over a down chain) holds nothing
        // answering: refresh it now instead of waiting for the next timer tick
        await pool.EnsureLiveAsync(ct).ConfigureAwait(false);

        Exception? lastError = null;
        foreach (var endpoint in pool.SnapshotAlive())
        {
            try
            {
                var answer = await DohClient.QueryAsync(socks, endpoint, host, timeout, ct);
                pool.MarkAlive(endpoint);
                return new ResolveResult(answer.Ips[0], endpoint.ToString(), answer.MinTtl);
            }
            catch (Exception ex) when (ex is IOException or FormatException or TimeoutException or SocketException)
            {
                pool.MarkDead(endpoint);
                lastError = ex;
            }
        }

        throw new IOException($"doh: all endpoints failed for {host}: {lastError?.Message}", lastError);
    }
}

/// <summary>
/// Cache on top of the resolver: the response TTL is clamped to [min, max] — short answers from
/// corporate resolvers do not force hitting DoH on every request, long ones do not hide an address
/// change.
/// </summary>
public sealed class CachingDnsResolver(IDnsResolver inner, TimeSpan ttlMin, TimeSpan ttlMax, TimeProvider? time = null)
    : IDnsResolver
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ResolveResult Result, DateTimeOffset Expires)> _cache = [];

    public async Task<ResolveResult> ResolveAsync(string host, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_cache.TryGetValue(host, out var hit) && hit.Expires > now)
            {
                return hit.Result;
            }
        }

        var resolved = await inner.ResolveAsync(host, ct);
        var ttl = resolved.TtlSeconds is { } seconds
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, (long)ttlMin.TotalSeconds, (long)ttlMax.TotalSeconds))
            : ttlMin;
        lock (_gate)
        {
            _cache[host] = (resolved, now + ttl);
        }

        return resolved;
    }
}
