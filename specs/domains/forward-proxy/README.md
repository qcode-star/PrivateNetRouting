# Forward Proxy

## Purpose

A local HTTP CONNECT proxy that puts every client (Claude Desktop policy, Claude Code/Codex
proxy env, the WSL sync, the updater, the health checks, the browser profile) behind one
address. It exists because the tailscale sidecar's own proxies resolve names with the system
resolver (the tsdial dialer never consults the netstack DNS) while the direct-access ban
blackholes exactly those names, and because the clients speak HTTP CONNECT, which a plain
SOCKS5 upstream does not answer. It is also the translator between the CONNECT world and the
SOCKS5 sidecar (or a trusted SOCKS5/HTTP upstream in the other chain modes).

## Key Files

- `src/PrivateNetRouting.Core/Net/Proxy/ForwardProxyService.cs` — the CONNECT proxy itself: listeners, per-connection pipeline, tunnel relay, WSL-gateway rebind, counters
- `src/PrivateNetRouting.Core/Net/Proxy/ForwardProxySupervisor.cs` — listener placement (in-process vs detached worker), busy-port rule, stop channel, kill semantics, handover
- `src/PrivateNetRouting.Core/Net/Proxy/ConnectRequestParser.cs` — CONNECT first-line parsing and host validation
- `src/PrivateNetRouting.Core/Net/Proxy/ConnectTargetPolicy.cs` — target refusal rules
- `src/PrivateNetRouting.Core/Net/Proxy/ProxyHealthCheck.cs` — CONNECT-roundtrip liveness check
- `src/PrivateNetRouting.Core/Net/Proxy/ProxyStopChannel.cs` — session-local stop requests to an in-process owner
- `src/PrivateNetRouting.Core/Net/Proxy/ProxyWorker.cs` — body of the `--proxy-worker` detached process

## Core Types

```csharp
public interface IProxyControl { bool IsRunning; Task StartAsync(CancellationToken); Task StopAsync(); }

public interface IProxyStatistics {
    bool IsRunning;
    IReadOnlyList<string> ListenerEndpoints;
    int ActiveConnections;
    long TotalConnections;
    long FailedConnections;
    bool CountersAreLocal => true;   // false when another process holds the port
}

public interface IForwardProxyInstance : IProxyControl, IProxyStatistics;

public sealed record ConnectRequest(string Host, int Port, bool IsProbe = false);
```

The supervisor (`ForwardProxySupervisor`) implements the same two interfaces over either the
in-process service or a detached worker, and adds `DetachAsync` (hand the listener to a
worker). In the skipped shapes it is an honest no-op (`skipped=true`): never running, nothing
to start/stop/detach.

## Flow

```
client -> ForwardProxyService.HandleClientAsync
           (detailed pipeline: connect-proxy.md)
        -> upstream (SOCKS5 to the sidecar listener or the trusted upstream)
```

Per-mode shape (decided by `chain.mode` and `forwardProxy.enabled`):

| Shape | Local proxy | Name resolution | Upstream dial |
|---|---|---|---|
| tailscale | always runs (off = refused bring-up) | DoH through the SOCKS5 upstream | by IP via `chain.tailscale.socksListen` |
| socks5 + enabled | runs | by the trusted upstream (name rides CONNECT) | `chain.socks5.upstream`, optional RFC 1929 |
| socks5 + disabled | does not exist | by the trusted upstream | clients point at `chain.socks5.upstream` itself |
| http | never runs (`forwardProxy.enabled` ignored) | by the provided HTTP proxy | clients point at `chain.http.upstream` |

## Invariants

- A busy port is bound by nobody: a supervisor that finds the port listening reuses the running
  owner and reports `IsRunning` from the port.
- Stop kills only a worker identified by the `--proxy-worker` marker in its command line; any
  foreign or unidentifiable owner is left alone with a warning.
- The proxy refuses (403) CONNECT to loopback/link-local/unspecified targets before any dial,
  including addresses a name resolves to where the proxy resolves names.
- Once `200 Connection Established` is on the wire, no proxy error bytes follow.
- A failed `StartAsync` leaves nothing bound (rollback of every listener it bound).
- `StartAsync`/`StopAsync`/`DisposeAsync` never capture the caller's `SynchronizationContext`
  (the GUI blocks on them).
- The WSL-gateway bind and the activity marker apply to every shape where the proxy runs, not
  to any mode in particular.

## Configuration

`forwardProxy.*` (`config/appsettings.example.json` is the authoritative schema):

| Key | Default | Notes |
|---|---|---|
| `enabled` | `true` | `false`: skipped shape in socks5, refused in tailscale, ignored in http |
| `listen` | `127.0.0.1:18081` | non-loopback logs a warning (no client authentication) |
| `bindWslGateway` | `true` | extra listener on the WSL NAT gateway, rebound on gateway change |
| `detached` | `false` | listener lives in a `--proxy-worker` process |
| `workerStartWaitSeconds` / `workerStopWaitSeconds` | 15 / 10 | worker spawn/stop polling bounds |
| `handoverLockWaitSeconds` | 60 | handover lock wait bound |
| `healthCheckHost` / `healthCheckPort` | public API host / 443 | CONNECT-roundtrip target |
| `socksTimeoutSeconds`, `headerTimeoutSeconds` | 12, 30 | upstream dial, client header read |
| `activityMarkerPath`, `activityTouchIntervalSeconds` | `%APPDATA%\PrivateNetRouting\proxy-activity`, 60 | see [Activity marker](../activity-marker.md) |

## Extension Points

- A new upstream transport: implement the dial as a method beside `Socks5Client` usage in
  `ForwardProxyService` and select it from the mode flag in `ProxyRuntime.FromConfig`.
- A new refusal rule: add it to `ConnectTargetPolicy` (pure static predicates — testable) and
  cover it in `ConnectTargetPolicyTests`.
- A new client of the proxy: take the address from `AppProxyEndpoint` (never hardcode
  `forwardProxy.listen`) — see [Configuration](../configuration/README.md).

## Related Specs

- [Connect proxy](connect-proxy.md) — the per-connection pipeline in detail
- [Listener supervisor](listener-supervisor.md) — placement, workers, stop channel, handover
- [DoH resolution](../doh-resolution.md) — name resolution in the tailscale mode
- [SOCKS5 client](../socks5-client.md) — upstream dialing
- [Chain](../chain/README.md) — the proxy is a chain link (`Proxy` step)
- [Protocols](../../contracts/protocols.md) — the CONNECT wire contract
