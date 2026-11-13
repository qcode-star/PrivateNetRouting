# Logging

## Purpose

One canonical file per log, shared by every process (GUI in the tray, `pnr` from a terminal,
detached workers), with levelled records and size rotation that loses nothing even across
crashes and concurrent writers.

## Key Files

- `src/PrivateNetRouting.Core/Logging/FileLog.cs` — `FileLog` + `LogRotation` (shift/lock/staging/orphan fold)
- `src/PrivateNetRouting.Core/Logging/LogLevel.cs` — `Debug`/`Info`/`Warn`/`Error` + parser
- `src/PrivateNetRouting.Core/Diagnostics/LogStore.cs` — list/tail/external rotation (see [Diagnostics](diagnostics.md))

## Core Types

```csharp
public sealed class FileLog {
    public FileLog(string filePath, LogLevel level, long maxBytes, int keep);
    public void Debug(string message); public void Info(string message);
    public void Warn(string message);  public void Error(string message);
    public bool RotateNow();   // manual: own log in place, free log by shifting
}
```

Line format: `yyyy-MM-dd HH:mm:ss TAG message` (local time, `DEBUG|INFO|WARN|ERROR`).
`logging.level` is the threshold (unknown value -> `info`). Log records are always English;
Debug carries periodic/per-request noise (watchdog ticks, DoH timers, proxy connects, chain
steps, download fragments), Info carries one-off events.

## Flow

```
write:   level < threshold? drop -> open <name>.log (AppendData, FileShare.ReadWrite|Delete)
         -> file >= maxBytes? RotateBySize (lock -> re-check -> stage -> shift chain -> fold orphans)
         -> append the line (kernel atomic append)

manual:  ProxyRuntime.RotateLog(name)
           live log of this process -> FileLog.RotateNow() (in place)
           free file               -> LogStore.Rotate (shift the chain)
           held by another process -> "locked" (honest answer)
```

## Behavior

**Shared file, no PID fallback**: every writer appends the same `<name>.log`; no handle is
held between records — each record opens with kernel-level append (`AppendData`) and
`FileShare.ReadWrite | Delete`. A log IO failure is swallowed (it never fails the reported
operation).

**Size rotation**: at construction (an overgrown file shifts, an undergrown one only folds
orphans) and before a record into an overgrown file (`>= maxBytes`). A held or failed shift
postpones to a retry interval (30 s) and writing continues; a later record retries — the
rotation is not lost until the next start.

**Rotation lock**: `<log>.rotation-lock` (`FileShare.None`, `DeleteOnClose`); contention
postpones like a held file. Under the lock the size is re-checked, so a log another process
just rotated is left alone.

**Staging and crash recovery**: a shift stages the current file as `<log>.rotating-<pid>-<random>`
(alive-foreign-pid staging = a live rotation, never touched). Orphan staging files (legacy
name, own/dead pid) are claimed by rename and appended to the end of `.1` **before** the age
chain shifts — no record is lost. A mid-shift failure moves the staged file back when nobody
re-created the current one.

**Age chain**: `.keep-1 -> .keep -> … -> .1`; the staged file becomes `.1`; beyond `keep` the
oldest is deleted. `RotateNow()` shifts the chain of the caller's own log in place (returns
false — "locked" — on a held file/chain); `ProxyRuntime.RotateLog(name)` dispatches: a live
log of this process rotates in place, a free file shifts via `LogStore`, a file held by
another process answers "locked".

## Error Handling

- Any log write failure is swallowed at the call site.
- A failed shift restores the staged records and postpones (retry interval).
- An unclaimable orphan staging file is skipped (kept for the next try).

## Invariants

- One file per log name for the whole system — no per-PID fallback, ever.
- Rotation loses no record: staging first, orphan fold before the shift.
- A size rotation of an already-rotated log shifts nothing (minBytes re-check under the lock).
- The threshold filter drops below-level records before any IO.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `logging.directory` | `%LOCALAPPDATA%\PrivateNetRouting\logs` | env vars expanded |
| `logging.level` | `info` | `debug`\|`info`\|`warn`\|`error`, unknown -> `info` |
| `logging.maxBytes` | 1000000 | size rotation threshold (>= 1) |
| `logging.keep` | 2 | age files kept (>= 1) |

Log names in use: `forward-proxy`, `chain`, `launcher`, `codex-launcher`, `browser`,
`watchdog`, `updater`, `codex-updater`, `guard`, `appupdate` (+ `chain-elevated.log` in the
pinned helper directory, `app-crash.log`, `forward-proxy.err.log`, `watchdog.err.log`,
`daemon.out/err.log`, `watch.err.log` as side logs).

## Extension Points

- A new subsystem log: add one `FileLog` in `ProxyRuntime.FromConfig` (the composition owns
  all ten) and keep the one-file-per-name rule.

## Related Specs

- [Diagnostics](diagnostics.md) — listing, tailing and manual rotation
- [Configuration](configuration/README.md) — `logging.*`
- [Layers](../architecture/layers.md) — who writes what
