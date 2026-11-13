# DoH Resolution

## Purpose

DNS-over-HTTPS (RFC 8484) name resolution for the tailscale mode: the forward proxy resolves
target names through DoH endpoints dialed over the sidecar's SOCKS5 upstream, because the
system resolver is poisoned by the direct-access ban and the tsdial dialer never consults the
netstack DNS. The endpoint pool is auto-filled from the tailnet netmap and optionally seeded
from config.

## Key Files

- `src/PrivateNetRouting.Core/Net/Dns/DnsWire.cs` — minimal RFC 1035 wire codec (A queries, response parse, NXDOMAIN typing)
- `src/PrivateNetRouting.Core/Net/Dns/DohClient.cs` — one DoH query over SOCKS5 (plain http or https)
- `src/PrivateNetRouting.Core/Net/Dns/DohEndpoint.cs` — `DohEndpoint` record + seed parsing
- `src/PrivateNetRouting.Core/Net/Dns/DohEndpointPool.cs` — pool: discovery, parallel probes, live-first ordering, cooldown, refresh
- `src/PrivateNetRouting.Core/Net/Dns/DohResolver.cs` — hedged multi-endpoint resolution + `CachingDnsResolver`
- `src/PrivateNetRouting.Core/Net/Dns/IDnsResolver.cs` — `IDnsResolver`, `ResolveResult`
- `src/PrivateNetRouting.Core/Net/Dns/TailscaleNetmapDohSource.cs` — `IDohEndpointSource` over `tailscale debug netmap`

## Core Types

```csharp
public sealed record DohEndpoint(IPAddress Ip, int Port, bool Secure = false, string? HostName = null);
// the dial host is always an IP; HostName feeds TLS SNI/cert check for named resolvers

public interface IDnsResolver {
    Task<ResolveResult> ResolveAsync(string host, CancellationToken ct);
}
public sealed record ResolveResult(IPAddress Ip, string Source, uint? TtlSeconds,
                                   IReadOnlyList<IPAddress>? Alternates = null);

public interface IDohEndpointSource {
    Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct);
}

public sealed record DnsAnswer(IReadOnlyList<IPAddress> Ips, uint? MinTtl);
```

## Flow

```
proxy pipeline -> CachingDnsResolver (TTL-clamped cache, single-flight, 10 s negative cache)
               -> DohResolver
                    1. EnsureLiveAsync (bounded wait; single-flight on-demand pool refresh)
                    2. candidates = pool.SnapshotAlive()
                    3. race candidates with 2 s hedge stagger; first positive answer wins,
                       cancels the losers; answer = first IP + up to 3 alternates
               -> DohClient.QueryAsync (per endpoint, over Socks5Client)
                    GET /dns-query?dns=<base64url>  (http: direct; https: SslStream, SNI from
                    HostName ?? IP) through the SOCKS5 upstream
```

Pool fill/refresh (`DohEndpointPool`): initial = `forwardProxy.dohSeed` (malformed entries
skipped with a Warn); when discovery is on, every refresh merges `[discovered, seed-not-in-
discovered]`; a discovery failure keeps the current pool beside the seed (endpoints found
earlier are never dropped). All merged endpoints are probed in parallel (default: resolve the
probe name; any failure counts dead, including negative answers — a route-only split-DNS
resolver sinks to the back). Result list is live-first; cooldowns clear on refresh. No
background work before the first request (`AnUnusedPoolDoesNoBackgroundWork`); an empty pool
retries on a backoff (15 s doubling to the refresh interval) and `EnsureLiveAsync` is
throttled (5 s minimum gap doubling to 15 s).

Netmap discovery (`TailscaleNetmapDohSource`): runs `<cliPath> --socket <socket> debug netmap`
(40 s hard timeout, stdout/stderr drained concurrently), extracts `http(s)://host[:port]/dns-query`
entries; IP literals kept as-is; a named host is resolved once at discovery with the system
resolver (resolver names are not part of the ban) and the name is kept for SNI; unresolvable
hosts are skipped (Warn once per distinct message).

## Error Handling

- `DnsNegativeAnswerException` (NXDOMAIN/NODATA): fails over to the next endpoint; a final
  negative is rethrown only when every tried endpoint answered negatively with no failures; a
  mix of failures becomes `IOException("doh: all endpoints failed …")`.
- Any other endpoint error (bad certificate included) -> `MarkDead` (cooldown) + failover.
- Only the caller's own cancellation aborts a resolution.
- A truncated DNS question/answer is an error, never an empty (cacheable NODATA) answer.
- HTTP non-200 from a DoH endpoint -> `FormatException` (counts as endpoint failure).

## Invariants

- The endpoint dial address is always an IP literal — system resolution never touches the
  endpoint itself.
- Negative DNS answers neither promote nor cool an endpoint.
- A CNAME chain's TTL lives only as long as its shortest link (every answer record's TTL folds
  into the minimum).
- The cache clamps TTLs into `[cacheTtlMinSeconds, cacheTtlMaxSeconds]` (inverted bounds swap);
  single-flight lookups are not tied to the first caller's token.
- A discovery failure never drops endpoints found earlier.
- The pool spawns no `tailscale` process and no probes until a resolution is asked for.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `forwardProxy.dohSeed` | `[]` | extra `http(s)://IP[:port]/dns-query` endpoints (IP-literal hosts only) |
| `forwardProxy.dohDiscovery.enabled` | `true` | netmap auto-fill (tailscale mode only) |
| `forwardProxy.dohTimeoutSeconds` | 8 | one DoH query |
| `forwardProxy.dohProbeTimeoutSeconds` | 4 | one pool probe |
| `forwardProxy.dohRefreshSeconds` | 600 | pool refresh period (floored to >= 1 s) |
| `forwardProxy.dohDeadCooldownSeconds` | 300 | cooldown of a dead endpoint |
| `forwardProxy.dohProbeName` | routed public name | probe query name |
| `forwardProxy.cacheTtlMinSeconds` / `cacheTtlMaxSeconds` | 60 / 3600 | answer TTL clamp |

## Extension Points

- A new endpoint source: implement `IDohEndpointSource` and pass it to the pool ctor (the
  netmap source is just one implementation).
- A different hedge/failover policy: `DohResolver` takes an injectable `query` delegate and
  `hedgeDelay`; keep the failover invariants above.

## Related Specs

- [Forward proxy](forward-proxy/README.md) — the consumer of `IDnsResolver`
- [SOCKS5 client](socks5-client.md) — every DoH dial goes through it
- [Chain](chain/README.md) — the netmap source uses the chain's CLI and socket
- [Protocols](../contracts/protocols.md) — DoH wire expectations
