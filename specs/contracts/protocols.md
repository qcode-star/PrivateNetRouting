# Contract: Proxy <-> Wire Protocols

## Boundary Rule

Clients and upstreams depend on the exact wire semantics below (HTTP CONNECT out of the local
proxy, SOCKS5 toward upstreams, DoH toward resolvers); these are contracts with processes we
do not control, so a change here is a breaking change for every client.

## Interfaces

| Protocol | Direction | Endpoint | Implemented in |
|---|---|---|---|
| HTTP CONNECT | client -> forward proxy | `forwardProxy.listen` (default `127.0.0.1:18081`) | `ConnectRequestParser`, `ForwardProxyService` |
| SOCKS5 (RFC 1928/1929) | forward proxy / DoH -> upstream | `chain.tailscale.socksListen` or `chain.socks5.upstream` | `Socks5Client` |
| DoH (RFC 8484) | forward proxy -> resolvers | `http(s)://IP[:port]/dns-query` | `DnsClient`, `DnsWire` |
| HTTP (plain GET) | updater -> feed/manifest | vendor update endpoints | `UpdateFeedUrlFinder`, `CodexVersionManifest` |

## Initialization

The proxy answers on the configured listener from `ForwardProxyService.StartAsync`; upstream
sockets are dialed per request; DoH endpoints come from the pool (seed + netmap discovery).

## Data Flow Across Boundary

CONNECT request line `CONNECT <authority> HTTP/1.1` + `Host:` header; the optional
`X-Pnr-Probe: 1` marker header means "serve but do not count as activity". Responses:
`200 Connection Established`, `400 Bad Request` (not a CONNECT / malformed authority),
`403 Forbidden` (forbidden target), `502 Bad Gateway` (upstream failure before the 200). After
the 200 the connection is an opaque byte tunnel (half-close aware).

SOCKS5: method offer (no-auth or RFC 1929 pair), CONNECT with ATYP 1 (IPv4) / 3 (name) / 4
(IPv6); REP != 0 is a refusal. DoH: `GET /dns-query?dns=<base64url>` with
`Accept: application/dns-message`; a 200 body is a DNS wire message; A records + min TTL are
the answer; NXDOMAIN/NODATA are negative answers.

## Error Propagation

- Pre-200 failures become the HTTP status codes above — never a raw socket close for a parsed
  request; unparsable input is closed without a response.
- After the 200 no proxy bytes are ever injected (a 502 would corrupt the client's TLS).
- SOCKS5/DoH protocol errors surface as `IOException`/`FormatException` at the caller (the
  proxy turns them into 502 or into a resolver failover).
- Parse error strings never echo URL paths/queries (token hygiene) and never carry control
  characters (log-forgery hygiene).

## Breaking Change Checklist

If you change X, you MUST also update Y:

- Change a status code, the probe header, or the accepted authority forms -> the client docs
  (`docs/claude-proxy.md`, `docs/codex-proxy.md`), `ConnectRequestParserTests`/
  `ForwardProxyEndToEndTests`, and the health-check expectations (`ProxyHealthCheckTests`).
- Change the SOCKS5 method/ATYP set -> `Socks5ClientTests` and every mode contract that names
  the addressing (the socks5 mode's "name rides CONNECT" rule).
- Change the DoH request/response handling -> `DohClientTests`/`DnsWireTests` and the pool
  probe semantics (what counts as a dead endpoint).
- Change the feed/manifest JSON shapes -> `UpdaterTests`/`CodexTests` and the version-compare
  fallbacks ("no version" must never hide an update).
