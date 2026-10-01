using System.Net.Sockets;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Резолв через пул DoH-эндпоинтов поверх SOCKS5-апстрима. Перебирает кандидатов,
/// отказавший помечает мёртвым (кулдаун в пуле); берётся первый успешный ответ.
/// </summary>
public sealed class DohResolver(Socks5Client socks, DohEndpointPool pool, TimeSpan timeout) : IDnsResolver
{
    public async Task<ResolveResult> ResolveAsync(string host, CancellationToken ct)
    {
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

        throw new IOException($"doh: все эндпоинты не ответили для {host}: {lastError?.Message}", lastError);
    }
}

/// <summary>
/// Кэш поверх резолвера: TTL ответа клампится в [min, max] — короткие ответы корпоративных
/// резолверов не вынуждают дёргать DoH на каждый запрос, длинные не прячут смену адреса.
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
