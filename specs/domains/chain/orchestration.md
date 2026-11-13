# Chain Orchestration

## Role

`ChainSupervisor` — the exact bring-up/stop sequences, the cross-process operation lock, the
cancellation contract, the raise/stop hooks, and the proxy handover.

## Key Files

- `src/PrivateNetRouting.Core/Chain/ChainSupervisor.cs` — `EnsureRunningCoreAsync`, `StopCoreAsync`, `HandOverProxyAsync`, `ChainRaisedLinks`

## Behavior

**Bring-up** (`EnsureRunningCoreAsync`) — mode fixed at construction (`config.EffectiveMode`;
unknown -> tailscale):

- *tailscale* (refuses immediately with `ChainException("proxy", …)` when
  `forwardProxy.enabled=false`):
  1. **Daemon** — skipped when the http listener answers and the process is alive; else mark
     raised, `StartNeedsUac()` decides the step hint ("starting (confirm UAC)"), start. A stale
     process without listener is killed and restarted (still counts as raised).
  2. **Keeper** — skipped when running; else mark raised, start.
  3. **Node** — when `BackendState == "Running"`: skipped. Else mark raised; on
     `"NoState"`/`"NeedsLogin"` run the interactive login (the auth URL rides progress as
     `ChainStep.AuthUrl` + "open this address to authenticate: …"; `"NeedsMachineAuth"` after
     login reports waiting for device approval), then `up` with the resolved control URL
     (configured `loginServer` -> node's own `ControlURL` -> system one -> default), then
     re-read: must be `"Running"`.
  4. **Proxy** (every non-skipped shape) — skipped when running; else mark raised, start.
  5. **Health** — CONNECT roundtrip every 1 s until `healthWaitSeconds` (each attempt capped to
     the remaining budget); success fires the `chainRaised` hook; deadline -> `ChainException("health")`.

- *socks5 / http*: **Egress** first (the upstream's own CONNECT check, polled to the same
  deadline; a hanging upstream ends as `ChainException("egress")`, an attempt-deadline timeout
  folds into "not reachable"), then **Proxy** (unless skipped), then **Health**.

**Stop** (`StopCoreAsync`) — strict reverse: `Proxy.stop -> cli.down -> daemon.kill ->
keeper.kill`; every step runs even after earlier failures (the first failure is raised as
`ChainException` at the end). No `down` against a dead daemon (the pipe decides). Stop links
receive `CancellationToken.None`. `StopRaisedAsync(raised, …)` stops exactly the links marked
in `ChainRaisedLinks` (rollback of a cancelled bring-up/launch).

**Rollback attribution**: `ChainRaisedLinks` marks each link *before* its start attempt, under
the lock. A bring-up cancelled while still waiting for the lock marks nothing and rolls back
nothing.

**Locking**: one operation at a time per process (`SemaphoreSlim(1,1)`) and per machine (lock
file `chain-operation.lock` in the chain log's directory; `FileMode.OpenOrCreate` +
`FileShare.None` + `DeleteOnClose` — a dead holder's handle dies with the process). The lock
wait has **no deadline** and honors the caller's token (progress `Lock`); a bring-up legitimately
holds it through a UAC prompt and an interactive login. An unopenable lock (ACL, read-only
dir) is logged and the operation runs without it (fail-open). A stop waits for the in-process
semaphore with `CancellationToken.None` ("a stop is never abandoned while it waits").

**Cancellation contract**: every link catch filters `OperationCanceledException` before
wrapping into `ChainException` — cancellation always surfaces unwrapped.

**Hooks**:
- `chainRaised` — fires only at the end of a fully successful bring-up (wired to the watchdog
  auto-start re-arm, fire-and-forget but tracked so a CLI can await it before exit).
- `chainStopped` — fires after a completed stop when the whole chain stopped cleanly or
  nothing of ours is running any more (wired to the watchdog stop). A rollback that leaves
  pre-existing links up does not fire it; a hook exception propagates unwrapped when the stop
  had zero step failures.

**Handover** (`HandOverProxyAsync(detach, lockWait, ct)`): waits (bounded by `lockWait`) for
both locks, then runs the detach (the supervisor's `DetachAsync`); past the bound it hands
over without the lock with a warning. Caller cancellation propagates.

## Error Handling

- One `ChainException(component, inner)` per failing link; the component names the link.
- A stop continues past failures and reports the first as the raised error.
- `GetStateAsync` hops to the thread pool (no `SynchronizationContext` capture) and composes a
  `ChainState` from live probes only — it never mutates anything.

## Invariants

- A concurrent second bring-up finds links up and attributes nothing to the waiting caller.
- A fully alive chain performs zero side effects on `EnsureRunningAsync`.
- The `Lock` step is the only progress component that can appear before any link.
- The health phase ends at its deadline even with hanging probes.
- `ChainState.IsFullyUp` requires `ProxyRunning != false` (null is "not applicable", not down).

## Related Specs

- [Chain](README.md) — shapes and vocabulary
- [Sidecar](sidecar.md) — what Daemon/Keeper/Node steps actually run
- [Listener supervisor](../forward-proxy/listener-supervisor.md) — the detach behind handover
- [Watchdog](../watchdog/README.md) — the hooks' consumers
