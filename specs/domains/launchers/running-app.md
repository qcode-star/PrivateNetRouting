# Running App

## Role

The shared already-running machinery for both desktop apps: state scan, log report, and the
graceful-then-forced close behind the restart-consent flow and the owner's manual stop.

## Key Files

- `src/PrivateNetRouting.Core/Launcher/RunningApp.cs` — `RunningApp`, `RunningAppInfo`, `RunningAppState`, `IRunningAppCloser`, `WindowsRunningAppCloser`, `RunningAppCloseResult`

## Behavior

**Scan** (`Inspect()`): find processes whose name contains the app's `processName`, require
each configured command-line marker to appear (Claude: the MSIX install-folder marker and the
alias path; Codex: the package name), parse `--proxy-server=` from the command line (up to
space/quote) and compare with our URL (case-insensitive):

| Condition | `RunningAppState` |
|---|---|
| no matching processes | `NotRunning` |
| every copy has our proxy URL | `Proxied` |
| any copy has a different/absent URL | `WithoutProxy` (+ first foreign URL) |

A scan failure logs Warn and reports `NotRunning`.

**Report** (`Report(info, appName)`): Info for `Proxied`, Warn for `WithoutProxy` (with the
foreign URL when seen), silent when not running. `NoteDeclined(appName)` logs a refused
restart.

**Close** (`CloseAsync(appName, purpose, graceful, ct)` -> `RunningAppCloseResult(ClosedGracefully,
Killed, Survived, Scanned)`): pin the scanned PIDs through real process handles first (PID-reuse safe;
an unpinnable PID is reported but never touched), send `WM_CLOSE` to every top-level window
except `IME`/`MSCTFIME UI` classes, wait `graceful` (`restartGraceSeconds`, 250 ms polls),
tree-kill the survivors, wait `killSettleSeconds` (3 s), report what survived. The `purpose`
("to restart through the proxy" / "on the owner's request") is what the log line says — the
same close serves both callers. `Scanned` is the whole family the scan found; children that go
down with a closed parent are counted there only, so `Closed = Scanned - Survived.Count` is the
honest number of processes the close removed (the window-close/kill split is not).

## Error Handling

- Scan failure -> Warn + `NotRunning` (never a crash, never a false "running").
- Unpinned (access-denied) PIDs are counted as existing but never closed or killed.
- Surviving PIDs are reported in the result, not retried.

## Invariants

- PID identity is held by handles, not by the number alone — a recycled PID is never killed.
- IME windows never receive `WM_CLOSE`.
- The close sequence is always graceful-first with a hard bound, then tree kill — never an
  immediate kill.
- The marker filter keeps a foreign process sharing the image-name spelling out of the verdict.

## Related Specs

- [Launchers](README.md) — who calls Inspect/Report/Close
- [App stop](app-stop.md) — the manual stop's Windows half
- [Updater](../updater/README.md) — the same close runs before an update install
- [Watchdog](../watchdog/wsl-app-stoppers.md) — the in-distro counterpart
