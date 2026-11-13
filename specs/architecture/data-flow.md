# Data Flow

## Context

Four lifecycles carry the system's traffic and control: a proxied CONNECT request, a chain
operation (bring-up/stop), the idle auto-stop cycle, and an application update run. Every other
flow is a variant of these. Knowing them end-to-end is what makes a change safe.

## CONNECT Request Lifecycle (forward proxy)

Applies in the shapes where the local proxy runs (tailscale mode always; socks5 mode with
`forwardProxy.enabled=true`). Detail: [Connect proxy](../domains/forward-proxy/connect-proxy.md).

```
client                    ForwardProxyService                upstream
  |                                |                              |
  |-- TCP connect ---------------->|                              |
  |-- CONNECT host:port HTTP/1.1 ->|                              |
  |                                | read header (cap 64 KiB,     |
  |                                |   headerTimeoutSeconds)      |
  |                                | parse first line ------------|--> 400 on non-CONNECT
  |                                | target policy ---------------|--> 403 on loopback/
  |                                |                              |    link-local/unspecified
  |                     [tailscale] resolve name via DoH pool     |
  |                                |   re-check every resolved IP |--> 403 if all forbidden
  |                     [socks5]   name rides the CONNECT         |
  |                                | dial upstream (SOCKS5,       |
  |                                |   ATYP 1/3/4) -------------->|
  |<-- HTTP/1.1 200 ----------------|                              |
  |                                | ActivityMarker.Touch()       |
  |                                |   (skipped for probes)       |
  |<======= byte relay (two 64 KiB pumps, half-close aware) =====>|
  |                                |                              |
  |            failure before 200 -> 502 + FailedConnections++    |
  |            failure after 200  -> close, no bytes injected     |
```

## Chain Operation Lifecycle

Detail: [Chain orchestration](../domains/chain/orchestration.md).

```
caller -> ChainSupervisor.EnsureRunningAsync(progress, ct)
  1. in-process semaphore (1,1)          — one operation per process
  2. cross-process lock file             — one operation per machine
     ("Lock" step, no deadline, honours ct)
  3. per-mode steps (each marks ChainRaisedLinks before starting):
       tailscale: Daemon -> Keeper -> [login] -> up -> Proxy -> Health
       socks5/http: Egress -> [Proxy] -> Health     (Proxy omitted when skipped)
  4. failure of a link -> ChainException(component); rollback stops
     only the links this call raised
  5. success -> chainRaised hook (watchdog auto-start re-arm)

StopAsync runs strict reverse order (Proxy -> down -> Daemon -> Keeper),
every step attempted even after earlier failures; links get
CancellationToken.None (a stop is never abandoned mid-way).
Cancellation of a bring-up surfaces as OperationCanceledException,
never wrapped in ChainException.
```

## Idle Auto-Stop Cycle

Detail: [Watchdog](../domains/watchdog/README.md).

```
every watchdog.checkSeconds:
  GetInUseReason(idleWindow)  -> busy?  (fail-closed: a throwing signal = busy)
  idle:
    stop app hosts in RUNNING configured WSL distros   (minutes)
    re-check activity (a client started meanwhile keeps the chain)
    Chain.StopAsync through the orchestrator
      success  -> stopper exits
      failure  -> count it; 3 consecutive failures -> give up + exit
```

## Update Run Lifecycle (Claude MSIX)

Detail: [Claude MSIX update](../domains/updater/claude-msix.md).

```
check versions (no chain, no UAC)
  -> UpToDate? stop (NoChange handling)
  -> find package URL (manual > feed over live proxy > AppX journal)
  -> ensure chain (health check, else raise)      — ActivityMarker kept fresh
  -> chunked download (resume manifest)           —  ActivityMarker kept fresh
  -> verify (identity/publisher/arch/age)
  -> close Claude -> Add-AppxPackage -> re-read version
     reached package version -> Installed; unchanged -> NoChange
```

The Codex Store update is the same skeleton with the Microsoft Store as the installer
([Codex Store update](../domains/updater/codex-store.md)).

## Invariants

- A CONNECT answered with `200 Connection Established` is never followed by proxy error bytes —
  after the 200 the stream belongs to the client's TLS.
- Health-probe requests (`X-Pnr-Probe` header) are served but never touch the activity marker.
- The names a CONNECT carries are resolved by the proxy (DoH, tailscale mode) or by the trusted
  upstream (socks5/http modes) — never by the poisoned system resolver along the chain path.
- Every chain link is marked raised before its start attempt; rollback stops exactly the raised
  links and nothing pre-existing.
- The idle stopper always re-checks activity after the WSL pass and before stopping the chain.
- A download keeps the activity marker fresh so auto-stop cannot kill the chain under it.

## Anti-Patterns

- **Resolving a target name with the system resolver inside the tailscale mode** — the direct-
  access ban blackholes exactly those names there.
- **Skipping the post-WSL activity re-check** — a client started during the minutes-long WSL
  pass would lose its chain.
- **Wrapping `OperationCanceledException` into `ChainException`** — the GUI stop-during-bring-up
  contract relies on the unwrapped form to distinguish cancel from failure.
- **Stopping only the failing link on a chain stop** — every stop step runs to completion even
  after earlier failures, or a daemon survives a failed `down`.
