# App Shell

## Purpose

The WPF host: one single-instance process per profile with a tray icon, the main window (chain
card, guards card, launch row), the update/diagnostics/profile windows, and the windowless roles
(detached proxy worker, idle stopper, elevated helper commands) that run before WPF starts.
Profiles: [Profiles](../configuration/profiles.md).

## Key Files

- `src/PrivateNetRouting.App/Program.cs` — `Main`: `HeadlessHost.TryRun` first, else WPF
- `src/PrivateNetRouting.App/HeadlessHost.cs` — windowless roles + crash logging
- `src/PrivateNetRouting.App/App.xaml` + `src/PrivateNetRouting.App/App.xaml.cs` — `ShutdownMode`, single-instance mutex + events, tray wiring, exit paths, startup checks
- `src/PrivateNetRouting.App/TrayIcon.cs` — the WinForms `NotifyIcon` menu and balloon
- `src/PrivateNetRouting.App/MainWindow.xaml` + `src/PrivateNetRouting.App/MainWindow.xaml.cs` — chain card, guards card, launch row (detail: [Chain card](chain-card.md))
- `src/PrivateNetRouting.App/DiagnosticsWindow.xaml`, `src/PrivateNetRouting.App/BrowserProfileWindow.xaml`, `src/PrivateNetRouting.App/AboutWindow.xaml` (+ code-behinds)
- `src/PrivateNetRouting.App/Theme.cs`, `WindowTheme.cs`, `App.xaml` — the UI token dictionary

## Core Types

```csharp
// Program.Main: HeadlessHost.TryRun(args, out exitCode) -> windowless roles; else WPF
public static class HeadlessHost;          // --proxy-worker | --watchdog-worker | --elevated-*
public partial class App : Application;    // mutex + events, tray wiring, exit paths
public sealed class TrayIcon : IDisposable;// WinForms NotifyIcon menu + balloon
```

## Flow

```
Program.Main
  -> HeadlessHost.TryRun(args):   --proxy-worker | --watchdog-worker | --elevated-*
       (thread-pool, no Application, no dispatcher) -> exit code
  -> new App(): --profile resolved (the identity) -> 3 named events created BEFORE the mutex
       (default names, or `.<profile>` for a named profile);
       every instance also holds Local\PrivateNetRouting.AnyInstance (the installer's AppMutex)
       first instance:  subscribe events (activation/launch verbs), build MainWindow,
                        wire tray, run 3 startup update checks (fire-and-forget)
       second instance: signal the matching (profile-scoped) event, Shutdown()
  -> Close/minimize hides to the tray (ShutdownMode=OnExplicitShutdown)
  -> real exit: tray menu only (or Windows session end)
```

Tray menu: show window | chain up / chain down / launch Claude (enablement mirrors the chain
card's buttons; no Codex tray item; the launch item disappears when `claude.enabled=false`) |
"Exit" | "Exit with full chain stop".

**Exits** (`ExitFromTrayAsync`, idempotent): cancel any bring-up/launch in flight and wait for
its rollback (the chain is left as it was before that operation); then either stop the chain
("Exit with full chain stop") or hand the in-process proxy listener over to a detached worker
("Exit") so the chain keeps serving. A started idle stopper outlives both exits (its own
process). An exiting instance ignores shortcut signals.

**Headless roles** (before WPF, from `Program.Main`): the `--proxy-worker` body, the
`--watchdog-worker` body, and the `--elevated-*` helper commands (`--result-file` required,
`--config`/`--env` parsed). Crash logging installs a fallback `app-crash.log` writer.

**Startup checks** (all fire-and-forget, all gated by config): the Claude update check
(2 s delay), the Codex update check (4 s), the app self-update check (2 s) — read-only, no
chain, no UAC.

## Invariants

- The 3 named events are created before the (profile-scoped) mutex (a racing second instance
  always finds them); `OnExit` disposes the mutexes before the events.
- Two profiles are two GUI processes: their mutexes/events differ, their titles and tray
  tooltips read `<product> — <profile display>`, and the installer sees any of them through
  `AnyInstance`.
- Closing or minimizing the window never stops the proxy or the idle stopper.
- The tray is a WinForms `NotifyIcon` referenced for that alone (global usings removed to
  avoid WPF name conflicts).
- All visual properties come from the `App.xaml` token dictionary
  (semantic brushes: `Window/Band/Card/Code`, text weights, `Accent`/`Codex` brand, state
  colors; `Space.*` on a 4 px grid; `Radius.Card/Control`; a four-step type scale) — windows
  reference keys, never values; a state is always a word next to the color.
- Machine data (addresses, paths, counters, log lines) is rendered in the monospaced face with
  tabular figures.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `ui.language` | `auto` | see [Localization](localization.md) |
| `ui.checkUpdatesOnStart` / `ui.checkAppUpdatesOnStart` | `true` | startup checks |
| `ui.chainCardExpanded` / `ui.guardsCardExpanded` | `true` | card collapse state |
| `claude.enabled` / `codex.enabled` | `true` | false hides the app's button pair, tray item and startup check |

## Extension Points

- A new tray action: reuse the chain card's command paths (same code as the buttons) and let
  enablement follow the card's flags.
- A new window: take the tokens from `App.xaml` via `Theme`, keep long waits off the UI thread
  (async/await + dispatcher queues).

## Related Specs

- [Chain card](chain-card.md) — the lifecycle control surface
- [Update windows](update-windows.md) — UpdateWindow / CodexUpdateWindow
- [Localization](localization.md) — language switching
- [Host <-> Core](../../contracts/host-core.md) — what the shell composes
- [CLI](../cli.md) — the other host
