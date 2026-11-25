# Listener Supervisor

## Role

Decides where the forward proxy's listener lives (this process or a detached worker) and
enforces the single-owner rule for the port across processes.

## Key Files

- `src/PrivateNetRouting.Core/Net/Proxy/ForwardProxySupervisor.cs` — placement, busy-port rule, stop channel, kill, `DetachAsync`
- `src/PrivateNetRouting.Core/Net/Proxy/ProxyWorker.cs` — `--proxy-worker` body (shared by `pnr` and the App)
- `src/PrivateNetRouting.Core/Net/Proxy/ProxyStopChannel.cs` — `IProxyStopChannel`, `NamedEventProxyStopChannel`

## Behavior

Shapes: `skipped=true` (http mode, or socks5 with `forwardProxy.enabled=false`) makes every
method an honest no-op — `IsRunning` false, counters zero, empty `ListenerEndpoints`, a log
line naming the external endpoint the applications point at.

Two liveness answers: `IsRunning` is port liveness (the in-process service, a detached worker
or another of our processes all count — the chain is alive either way), while
`IsRunningInThisProcess` is true only for the in-process service. The diagnostics use the latter
to decide whether the pid they print is their own; a listener owned elsewhere is reported with the
owner's pid from the TCP table.

**Start** (`StartLockedAsync`):
1. Already running -> return (idempotent).
2. Port already listening -> log "already listening — reusing the running owner"; never bind.
   Liveness for the chain is the port, whoever holds it.
3. `forwardProxy.detached` -> spawn the worker; else start the in-process service and begin
   listening for stop requests.

**Worker spawn** (`StartWorkerAsync`): command line `{currentExe} --proxy-worker --listen
{config.Listen}` (`WorkerMarker`, `ProxyWorker.ListenOption`), spawned through `cmd` with
stdout to NUL and stderr to `forward-proxy.err.log` beside the log directory. The `--listen`
value overrides the worker's own disk config. The supervisor polls the port every 250 ms up to
`workerStartWaitSeconds`; the cmd pid dying first is an immediate `IOException` pointing at
the error log; timeout/cancellation kills the spawned tree and throws.

**Stop** (`StopLockedAsync`):
1. In-process service running -> stop it.
2. Else enumerate the port's owners (`ITcpConnectionTable`): PIDs whose command line contains
   `--proxy-worker` are "our worker" -> kill whole tree, wait up to `workerStopWaitSeconds`
   for the port to release (250 ms polling; a still-busy port warns).
3. No worker identified -> `RequestOwnerStop()` on the stop channel: an in-process owner of
   ours (the GUI or a foreground `pnr proxy`) is asked to stop and waited for. A foreign or
   unidentifiable owner (e.g. an elevated process hidden from non-elevated WMI) is left
   running with a warning.

**Stop channel** (`NamedEventProxyStopChannel`): a session-local AutoReset named event
`Local\PrivateNetRouting.ProxyStop.{port}`. The owner registers `Listen(port, callback)`; the
asker calls `Request(port)` (true only if an owner was listening). The supervisor serializes a
remote stop request with its own lifecycle under one semaphore and drops stale ones: a request
aimed at a listener that already went away must not stop its successor. Signalling failures
(`UnauthorizedAccess`, `IOException` — an elevated owner) are treated as foreign.

**Detach/handover** (`DetachAsync`): stop listening for stop requests, stop the in-process
service, spawn the worker — clients see only a short gap. If the port is already held (by a
worker or anyone), leave it as-is. The chain-level wrapper
`ChainSupervisor.HandOverProxyAsync` runs this under the chain operation lock, bounded by
`forwardProxy.handoverLockWaitSeconds` (past the bound: hand over without the lock + warning).

## Error Handling

- Spawn timeout/cancellation kills the spawned tree before throwing.
- A worker that dies before binding surfaces as `IOException` with the stderr log name.
- A stale stop request is ignored (logged Debug), never applied to a newer listener.
- Stop tolerates a foreign owner (warning only) — it is never killed.

## Invariants

- Exactly one owner of the port at a time; a second start reuses rather than binds.
- Only a process identified by the `--proxy-worker` marker in its command line is ever killed —
  and its profile (`--profile`, none for the default profile) must match this profile's: a
  named profile never adopts or stops another profile's worker; a busy port owned by another
  process is an explicit error there, not a silent route through the wrong chain
  ([Profiles](../configuration/profiles.md)).
- `IsRunning` answers from the port, identically for GUI and CLI.
- `CountersAreLocal` is false when another process holds the port (the counters would describe
  only this process's idle instance).
- The handover never happens mid-stop of another process's chain operation: it waits for the
  chain lock first (bounded).

## Related Specs

- [Forward proxy](README.md) — what the listener serves
- [Chain orchestration](../chain/orchestration.md) — `HandOverProxyAsync` lock semantics
- [App shell](../app-shell/README.md) — tray "Exit" handover
- [CLI](../cli.md) — commands that leave a client behind and hand over
