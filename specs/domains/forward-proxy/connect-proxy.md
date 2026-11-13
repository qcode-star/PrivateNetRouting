# Connect Proxy

## Role

The per-connection pipeline of the forward proxy: parse the CONNECT request, refuse forbidden
targets, resolve and dial the upstream, then relay bytes.

## Key Files

- `src/PrivateNetRouting.Core/Net/Proxy/ForwardProxyService.cs` — `HandleClientAsync` pipeline, `RunAcceptLoopAsync`, tunnel relay, WSL bind loop
- `src/PrivateNetRouting.Core/Net/Proxy/ConnectRequestParser.cs` — `ConnectRequest`, `ConnectRequestParser.TryParse`
- `src/PrivateNetRouting.Core/Net/Proxy/ConnectTargetPolicy.cs` — `IsForbidden(host)`, `IsForbidden(address)`
- `src/PrivateNetRouting.Core/Net/WslGateway.cs` — `FindIpv4()` (the WSL NAT gateway address)

## Behavior

Pipeline order per accepted connection (`HandleClientAsync`):

1. **Header read** — up to `\r\n\r\n`, hard cap `MaxHeaderBytes` = 64 KiB, deadline
   `headerTimeoutSeconds`. A client that never completes the request is closed quietly
   (Debug log, no HTTP answer, not a failed connection). Oversized header: silent close.
2. **Parse** (`ConnectRequestParser.TryParse`) — request line must be exactly three
   space-separated parts, method `CONNECT` (case-insensitive), terminated by `\r`. Authority:
   bracketed IPv6 literal, `host:port`, or bare host (default port 443); port 1..65535. Accepted
   host forms: bracketed IPv6 literal without zone, canonical dotted-quad IPv4 only (platform
   shorthands like `1` or `0x7f.1` are rejected), or an LDH DNS name (ASCII letters/digits/`-`/`_`,
   1..63 bytes per label, <= 253 total). Host is normalized (trim trailing dot, lowercase).
   The header block is scanned for `X-Pnr-Probe` -> `IsProbe`. Parse errors never echo URL
   paths/queries (token hygiene).
3. **Target policy** (`ConnectTargetPolicy`) — forbidden before any dial: `localhost` /
   `*.localhost`; IP literals that are loopback, unspecified (`0.0.0.0`, `::`, the whole
   `0.0.0.0/8`), `255.255.255.255`, IPv4 link-local `169.254.0.0/16`, IPv6 `fe80::/10`;
   IPv4-mapped IPv6 is mapped down first. Forbidden -> `403 Forbidden` + Warn. Private ranges
   and the tailnet stay allowed.
4. **Upstream dial** — two per-mode paths (constructor flag `passHostNameToUpstream`, set only
   for the socks5 mode):
   - socks5 mode: `_socks.ConnectAsync(target.Host, target.Port, …)` — the name rides the
     CONNECT (ATYP=3), the trusted upstream resolves it; the resolved-IP refusal deliberately
     cannot apply here.
   - tailscale mode: the name is resolved through the DoH chain
     (`CachingDnsResolver` over `DohResolver`); an IP literal short-circuits. Every resolved
     address (primary + alternates) is re-checked with `ConnectTargetPolicy` (DNS-rebinding
     defense): all forbidden -> `403`; some forbidden -> Warn and dial only the allowed ones
     (`DialFirstAsync`, first success wins, last failure propagates).
5. **Established** — write `HTTP/1.1 200 Connection Established`, touch the activity marker
   unless `target.IsProbe`, enable TCP keepalive on both sockets (best-effort), forward bytes
   pipelined behind the CONNECT header first, then `TunnelAsync`: two 64 KiB pumps until both
   directions end. Clean EOF of one direction is a half-close (only the sending side shuts
   down); a failure or service stop shuts both down.
6. **Errors** — any failure before the 200 -> `502 Bad Gateway` + `FailedConnections`++ +
   Warn. After the 200 (`established`) no proxy bytes may follow: the connection is closed
   with a Debug log and is not a failed CONNECT. Service-stop cancellation closes silently.

WSL bind (`BindWslGateway`, default on): a background loop for the life of the start binds the
same port on `WslGateway.FindIpv4()` every 30 s — rebinds when the gateway address changes
(recreated WSL NAT network), drops the listener when the gateway disappears. A non-loopback
`listen` address logs a warning on start (anyone who reaches it can use the chain).

Accept loop: backlog 128; a socket error backs off (50 ms doubling to 2 s, warn once per
error code); a successful accept resets the backoff; listener shutdown ends the loop quietly.

## Error Handling

- HTTP status vocabulary out of the proxy: `400` (not a valid CONNECT request), `403`
  (forbidden target), `502` (upstream failure before the 200). Nothing else.
- Parse error strings cut at `/`, `?`, `#` and truncate — a header cannot smuggle tokens or
  control characters into the log.
- `StartAsync` failure rolls back every listener already bound; the service remains restartable.
- A resolver missing where the tailscale mode needs it surfaces as `502` (`InvalidOperationException`
  inside the pipeline).

## Invariants

- The pipeline refuses a forbidden target before any upstream dial.
- A name that resolves (partly) into forbidden addresses is never dialed at those addresses.
- Probe-marked requests never touch the activity marker.
- Bytes arriving behind the CONNECT header belong to the tunnel, not the parser.
- A half-closed direction delivers the remaining answer (no truncation on clean client EOF).
- Counters: `TotalConnections` counts accepts, `FailedConnections` counts pre-200 failures
  only.

## Related Specs

- [Forward proxy](README.md) — domain overview and per-mode shapes
- [Listener supervisor](listener-supervisor.md) — who owns the listener
- [DoH resolution](../doh-resolution.md) — the resolver behind step 4
- [Protocols](../../contracts/protocols.md) — wire semantics relied on by clients
