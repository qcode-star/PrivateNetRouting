# Diagnostics

## Purpose

A read-only state summary (who listens on the chain ports, keeper/node or upstream, WSL bind,
counters, marker age, a detailed CONNECT roundtrip, the guard core, the idle stopper) shown in
the GUI and by `pnr status` — plus the log list/tail/rotate surface of `pnr logs`.

## Key Files

- `src/PrivateNetRouting.Core/Diagnostics/DiagnosticsService.cs` — `DiagnosticsService`, `DiagLine`, `DiagSection`, `DiagnosticsSummary`
- `src/PrivateNetRouting.Core/Diagnostics/LogStore.cs` — `LogStore`: `List`, `ReadTail`, `Rotate`

## Core Types

```csharp
public enum DiagMark { Neutral, Good, Warn, Bad }
public sealed record DiagLine(string Name, string Value, DiagMark Mark);
public sealed record DiagSection(string Key, IReadOnlyList<DiagLine> Lines);
// stable keys: chain | proxy | health | guards | watchdog | logs
public sealed record DiagnosticsSummary(bool ChainStopped, IReadOnlyList<DiagSection> Sections);
```

## Flow

`GetSummaryAsync` (whole summary under one 45 s deadline) composes:

- **chain**: daemon http/socks listeners with owner PIDs (TCP table, no PowerShell), the
  keeper (`watch-ipn` match -> `pid …`, else Warn "the chain may fall"), the node
  (`BackendState`); in socks5/http modes a single **egress** line instead (the upstream,
  the socks5 one with its user and never the password, plus reachability).
- **proxy**: running-with-pid / "port held by another process of ours" / stopped; listener
  list; the WSL bind (flagged when stale vs the current gateway); connection counters (or
  "n/a — the proxy lives in another process"); the activity marker's age.
- **health**: the CONNECT roundtrip `OK in N ms (first response line)` / `failed in N ms: …`;
  skipped when no probe is wanted.
- **guards**: egress policy + local ban core (without WSL/settings).
- **watchdog**: idle stopper running (`pid …`) or not.
- **logs**: directory + file count.

A fully stopped chain answers the short "normal" message instead (rendered by the consumers):
nothing is running and that is expected while neither app is needed. In the skipped shapes the
forward-proxy port is not consulted; the proxy section says "skipped — the applications point
at …" and the CONNECT roundtrip runs against the applications endpoint.

`LogStore`: `List()` (files matching `*.log`/`*.log.<digits>`), `ReadTail(name, lines)`
(backwards chunked read, path traversal rejected), `Rotate(name, keep)` (shift a free file —
"rotated" / "unknown log name" / "no such log file" / "locked by another process").

## Error Handling

- The summary never starts or stops anything — every section degrades to a line with the
  reason instead of failing the snapshot.
- A whole-summary deadline overrun throws `TimeoutException` (the window reports it).
- A foreign owner of the proxy port is reported as "another process", not as a stopped chain.

## Invariants

- The summary is strictly read-only: no binds, no kills, no config writes.
- A socks5/http stopped chain ignores the tailnet daemon ports entirely.
- The socks5 upstream's password never appears anywhere in the summary.
- The "stopped chain" message is a normal state, rendered as such by both hosts.

## Configuration

No dedicated keys; the summary reads the live state plus `logging.directory` (logs section).

## Extension Points

- A new summary fact: add a `DiagLine` to the matching `DiagSection` with a `DiagMark`
  (the GUI colors by mark, the CLI prints the word) — keep the section keys stable, the
  hosts localize headers by them.

## Related Specs

- [Logging](logging.md) — what `LogStore` lists and rotates
- [Chain](chain/README.md) — the state the chain section reports
- [Guard](guard/README.md) — the guards core section
- [App shell](app-shell/README.md) — the DiagnosticsWindow consumer
