# App Stop

## Role

The owner's manual stop of one desktop app: its processes on Windows and its hosts inside the
running configured WSL distros, exposed as `ProxyRuntime.AppStopper` and the two buttons under
the launch row.

## Key Files

- `src/PrivateNetRouting.Core/Launcher/AppStopper.cs` — `AppStopper`, `AppStopTarget`, `AppStopResult`
- `src/PrivateNetRouting.Core/Launcher/RunningApp.cs` — the Windows close (`RunningApp.CloseAsync`, `IRunningAppCloser`)
- `src/PrivateNetRouting.Core/Watchdog/WslAppHost.cs` — the WSL pass (`WslAppHostStopper`, one app's flags)
- `src/PrivateNetRouting.App/MainWindow.xaml` + `.xaml.cs` — the stop row, `RunStopAppAsync`, `ReportAppStop`

## Behavior

```
StopAsync(target, ct):
  target app disabled in the config -> Disabled result, nothing is touched
  Windows: target launcher's CloseRunningAsync
           (RunningApp scan+close, purpose "on the owner's request",
            graceful bound restartGraceSeconds, kill settle 3 s)
  WSL:     only after the Windows close returned — a closing app may restart its WSL host
           while its own process is still alive, so the pass runs over the post-close state
           WslAppHostStopper(distros,
               stopClaude: target == Claude, stopCodex: target == Codex)
           only already running configured distros, one pass,
           120 s per distro; no distros configured -> Wsl = null
  WSL outcome -> the target's launcher log (Warn for Stuck)
  -> AppStopResult(Target, Disabled, Windows, Wsl, Elapsed)
```

`AnythingStopped` is true when a Windows process was closed or a WSL host was stopped;
`SomethingSurvived` — when Windows survivors remain or the WSL pass is `Stuck`.

The GUI offers both buttons while the app is enabled and no chain/launch/stop operation runs,
grays them out while a stop runs (a launch waits for it), and reports on the result line:
"nothing to stop", the closed process count plus the WSL word (`no hosts running` / `stopped` /
`not confirmed` / `no WSL distros configured`), or the partial outcome. The operation is not
cancellable, has no operation slot and never paints the chain rail.

## Error Handling

- A scan failure inside `RunningApp` reports "not running" with a Warn and closes nothing.
- A `wsl.exe` failure faults the WSL half; the GUI reports it via `Main.ErrorFmt`, the Windows
  half may still have completed.
- A wedged distro is reported `Stuck` after its timeout and the next distro is still visited.
- A survivor of the forced kill is reported, never retried.

## Invariants

- Only the target app's own processes are touched: the marker filters keep CLI processes that
  share the image name (`claude.exe`, a manual codex CLI) and the other desktop app out.
- Only already running configured distros are visited; no distro is booted for a stop.
- The chain is never touched by this action.
- A stop that has started is not cancelled by the GUI; a launch waits for it.
- The desktop apps are machine-wide: a stop from any profile's window stops that app for every
  profile.

## Related Specs

- [Launchers](README.md) — the launch/restart flow and its scoped consent rule
- [Running app](running-app.md) — the Windows close contract
- [WSL app stoppers](../watchdog/wsl-app-stoppers.md) — the in-distro pass reused here
- [Chain card](../app-shell/chain-card.md) — the UI row and the busy interlocks
- [ADR-008](../../decisions/008-manual-app-stop.md)
