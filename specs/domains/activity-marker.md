# Activity Marker

## Purpose

A tiny mtime-based signal that says "proxied traffic happened recently": the idle stopper
reads it to keep the chain alive under traffic the process scan cannot see, and diagnostics
reports its age. The updater refreshes it during downloads so auto-stop cannot kill the chain
under a running download.

## Key Files

- `src/PrivateNetRouting.Core/Runtime/ActivityMarker.cs` — `ActivityMarker(string path, TimeSpan minInterval, TimeProvider? time = null)`

## Core Types

```csharp
public sealed class ActivityMarker {
    public string Path { get; }
    public void Touch();   // creates the file on first use, then stamps mtime
}
```

## Flow

```
writers                                    readers
  ForwardProxyService   after the 200      ClaudeUsageProbe   freshness = activity
    (skips IsProbe)      of every CONNECT  DiagnosticsService age / absence line
  ClaudeUpdater         before + during
                        each download tick
```

`Touch()` creates the parent directory and an empty file on first use, then only stamps the
mtime (UTC). Writes are throttled to one per `minInterval` on the **monotonic** clock (a
wall-clock step neither suppresses touches nor lets a burst through). Filesystem errors are
swallowed — the marker is auxiliary.

Reading side (`ClaudeUsageProbe`): a marker counts as activity when `age < idleWindow` and
`age > -5 min` (a far-future stamp is no proof of activity). A missing/unreadable marker is
simply no signal.

## Error Handling

- `Touch()` never throws (`IOException`/`UnauthorizedAccessException` swallowed).
- A read failure is no signal (the watchdog falls through to "idle" on this source only when
  every source answered — see [Usage probe](watchdog/usage-probe.md)).

## Invariants

- The file's mtime equals the time of the last proxied (non-probe) request or download tick.
- Probe traffic (`X-Pnr-Probe`) never touches the marker.
- The marker signal is skipped entirely (null path) in skipped shapes where no local proxy runs.
- Throttling uses the monotonic clock only.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `forwardProxy.activityMarkerPath` | `%APPDATA%\PrivateNetRouting\proxy-activity` | env vars expanded |
| `forwardProxy.activityTouchIntervalSeconds` | 60 | throttle floor |
| `watchdog.checkSeconds` | 600 | also the freshness window on the read side |

## Extension Points

- A new activity source: call `Touch()` on meaningful traffic only (never per-tick noise) and
  make sure the corresponding probe path counts it; probe-style traffic must stay excluded.

## Related Specs

- [Forward proxy](forward-proxy/connect-proxy.md) — the primary writer
- [Watchdog](watchdog/usage-probe.md) — the reader whose idle verdict depends on it
- [Updater](updater/claude-msix.md) — keeps it fresh during downloads
- [Diagnostics](diagnostics.md) — reports its age
