# Chain

## Purpose

Idempotent bring-up and shutdown of the whole proxy chain in one of three shapes picked by
`chain.mode`: the userspace tailscale sidecar (default), a trusted external SOCKS5 upstream, or
a provided plain HTTP proxy. The chain is the single lifecycle unit the GUI card, the CLI, the
launchers, and the watchdog operate on.

## Key Files

- `src/PrivateNetRouting.Core/Chain/ChainSupervisor.cs` — orchestration, operation lock, state, hooks, proxy handover; `ChainState`/`ChainStep`/`ChainRaisedLinks`/`IChainSupervisor`/`IProxyControl`/`ChainException`
- `src/PrivateNetRouting.Core/Chain/SidecarDaemon.cs` — userspace tailscaled lifecycle
- `src/PrivateNetRouting.Core/Chain/SidecarKeeper.cs` — `tailscale debug watch-ipn` keeper
- `src/PrivateNetRouting.Core/Chain/SidecarCli.cs` — `tailscale --socket <pipe> …` wrapper
- `src/PrivateNetRouting.Core/Chain/TailscaleVersionProbe.cs` — the elevation decision's version input
- `src/PrivateNetRouting.Core/Chain/SystemControlUrlFinder.cs` — the system daemon's ControlURL (read-only)
- `src/PrivateNetRouting.Core/Chain/ElevatedChain.cs` — elevated start/stop commands (see [Elevated helper](../elevated-helper.md))

## Core Types

```csharp
public interface IChainSupervisor {
    Task<ChainState> GetStateAsync(CancellationToken ct);
    Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct);
    Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct);
    // + defaulted overloads taking ChainRaisedLinks for rollback-scoped stops
}

public sealed record ChainState(bool DaemonHttpListening, bool DaemonProcessAlive,
    bool KeeperRunning, string? BackendState, bool? ProxyRunning, bool ProxyHealthy,
    bool? UpstreamReachable = null);
// IsFullyUp / IsStopped are computed; ProxyRunning is null ("not applicable") in skipped shapes

public sealed record ChainStep(string Component, ChainStepStatus Status, string Detail,
    bool NeedsUac = false, string? AuthUrl = null);
// Component vocabulary: "Lock","Daemon","Keeper","Node","Proxy","Health","Egress"
// Status: Pending | Active | Done | Skipped | Failed

public sealed class ChainException(string component, Exception inner) : Exception;
// one per link: "daemon" | "keeper" | "node" | "proxy" | "health" | "egress"
```

## Flow

Per-mode step order (detail in [Orchestration](orchestration.md)):

```
tailscale :  Daemon -> Keeper -> [login/up] -> Proxy -> Health
socks5/http: Egress -> [Proxy] -> Health        (Proxy omitted when the local proxy is skipped)
stop      :  Proxy -> down -> Daemon -> Keeper  (strict reverse, every step attempted)
```

Mode shapes:

| `chain.mode` | What runs | Notes |
|---|---|---|
| `tailscale` | userspace daemon + keeper + node + local proxy | DoH resolution; `forwardProxy.enabled=false` is refused |
| `socks5` | external SOCKS5 upstream + (usually) local proxy | names ride the CONNECT; no DoH machinery built |
| `http` | external HTTP proxy only | no local proxy anywhere (`forwardProxy.enabled` ignored) |

## Invariants

- Every operation is idempotent: links already up are `Skipped` and recorded in no
  `ChainRaisedLinks`.
- Cancellation surfaces as `OperationCanceledException`, never wrapped in `ChainException`
  (the GUI stop-during-bring-up contract).
- A stop runs its links with `CancellationToken.None` — a cancelled stop still completes.
- Operations of different processes serialize on a lock file next to the chain log; a waiter
  waits without a deadline on its own token.
- The tailscale mode refuses to bring up when `forwardProxy.enabled=false` (the proxy is
  load-bearing there); the skipped shapes report `ChainState.ProxyRunning = null`.
- Every `tailscale up` mentions the control server the node is bound to (tailscale rejects an
  `up` that omits a non-default one).

## Configuration

`chain.*` (authoritative schema: `config/appsettings.example.json`):

| Key | Default | Notes |
|---|---|---|
| `mode` | `tailscale` | `tailscale` \| `socks5` \| `http`; a change applies after a restart |
| `healthWaitSeconds` | 90 | deadline of the CONNECT-roundtrip phase |
| `tailscale.*` | — | paths, `stateDir`, `socket` (named pipe), `socksListen`/`httpListen`, `elevation`, `loginServer`, timing bounds (`daemonWaitSeconds`, `upTimeoutSeconds`, `loginWaitSeconds`, `cliTimeoutSeconds`, `uacWaitSeconds`) |
| `socks5.upstream` / `.user` / `.password` | `127.0.0.1:18083` / empty | trusted upstream of the socks5 mode (RFC 1929 pair) |
| `http.upstream` | placeholder | the http mode's CONNECT-capable proxy (`IP:port`) |

## Extension Points

- A new chain shape: add a `ChainMode` value, branch the step order in `EnsureRunningCoreAsync`,
  teach `AppProxyEndpoint` and the rail stages (`ChainRail` label keys) about it, and cover the
  shape in `ChainSupervisorTests`.
- A new link in the bring-up: give it a component name, mark it in `ChainRaisedLinks` before
  starting, and add the matching stop step in strict reverse.

## Related Specs

- [Orchestration](orchestration.md) — bring-up/stop order, lock, hooks, handover
- [Sidecar](sidecar.md) — daemon/keeper/CLI internals of the tailscale mode
- [Elevated helper](../elevated-helper.md) — the only UAC path
- [Forward proxy](../forward-proxy/README.md) — the `Proxy` link
- [Configuration](../configuration/README.md) — `AppProxyEndpoint` derivation per mode
