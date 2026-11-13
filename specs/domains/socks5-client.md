# SOCKS5 Client

## Purpose

The RFC 1928 SOCKS5 client that dials every upstream of the tailscale mode: target addresses
for the forward proxy, DoH endpoints, and the chain egress checks. Optionally authenticates
with RFC 1929 user/pass (the socks5 mode's trusted upstream).

## Key Files

- `src/PrivateNetRouting.Core/Net/Socks/Socks5Client.cs` — `Socks5Client`, `ParseEndPoint`

## Core Types

```csharp
public sealed class Socks5Client {
    public Socks5Client(string upstream, string? user = null, string? password = null);
    public Socks5Client(IPEndPoint upstream, string? user = null, string? password = null);

    // ATYP 1 (IPv4) / ATYP 4 (IPv6); an IPv4-mapped IPv6 destination is dialed as IPv4
    public Task<Socket> ConnectAsync(IPAddress destination, int port, TimeSpan timeout, CancellationToken ct);

    // ATYP 3; an IP-literal name still dials by IP (ATYP 1/4)
    public Task<Socket> ConnectAsync(string hostName, int port, TimeSpan timeout, CancellationToken ct);

    public static IPEndPoint ParseEndPoint(string hostPort);
}
```

## Behavior

- Method negotiation offers exactly one method: no-auth (`0x00`) when no credentials are
  configured, RFC 1929 (`0x02`) otherwise. Empty strings mean no auth; only one of
  user/password set is an `ArgumentException` at construction.
- Dial by IP: IPv4 -> ATYP=1, IPv6 -> ATYP=4; other address families -> `NotSupportedException`.
- Dial by name: ATYP=3, UTF-8 name 1..255 bytes (`ArgumentException` beyond).
- Port must be 1..65535 (`ArgumentOutOfRangeException`).
- `ParseEndPoint` accepts `host:port` where the host must be an IP literal (`FormatException`
  otherwise — an upstream is always an IP from the config, never resolved); the port may be 0
  (ephemeral listen).

## Flow

```
caller -> new Socks5Client(upstream, user?, password?)   // IP:port literal only
       -> ConnectAsync(IPAddress | hostName, port, timeout, ct)
            1. TCP connect to the upstream
            2. method offer (0x00 no-auth, or 0x02 RFC 1929 when credentials are set)
            3. [RFC 1929] username/password roundtrip
            4. CONNECT request: ATYP 1 (IPv4) / 3 (name) / 4 (IPv6)
            5. reply parse (REP must be 0)
       -> Socket handed to the caller for byte relay
```

## Error Handling

| Condition | Outcome |
|---|---|
| Upstream rejects the offered method | `IOException` |
| Upstream rejects credentials | `IOException` |
| Reply wrong version / non-zero RSV | `IOException` ("malformed reply"; the stream is desynchronized) |
| REP != 0 (connect rejected) | `IOException` ("connect rejected (rep=N)") |
| Unknown bound ATYP | `IOException` |
| Premature close mid-handshake | `IOException` ("upstream closed the connection early") |
| Timeout | `OperationCanceledException` from the linked token |

The socket is disposed on any failure.

## Invariants

- Exactly one auth method is offered per handshake; credentials must be set together.
- A name that parses as an IP literal dials by IP, never by name.
- The upstream endpoint itself is parsed from an IP literal only.
- No DNS resolution ever happens inside this client.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `chain.socks5.upstream` | `127.0.0.1:18083` | socks5 mode trusted upstream (`IP:port`) |
| `chain.socks5.user` / `chain.socks5.password` | `""` / `""` | RFC 1929 pair; both empty = no auth |
| `chain.tailscale.socksListen` | `127.0.0.1:18083` | sidecar listener (tailscale mode; derived from the daemon, no auth) |
| `forwardProxy.socksTimeoutSeconds` | 12 | handshake + dial bound |

## Extension Points

- A new upstream shape: add a `ConnectAsync` overload for its addressing and keep the error
  vocabulary (`IOException` for protocol refusals, `OperationCanceledException` for timeouts).

## Related Specs

- [Forward proxy](forward-proxy/README.md) — dials targets through this client
- [DoH resolution](doh-resolution.md) — dials DoH endpoints through this client
- [Chain](chain/README.md) — owns which upstream address is configured per mode
- [Protocols](../contracts/protocols.md) — the SOCKS5 wire contract
