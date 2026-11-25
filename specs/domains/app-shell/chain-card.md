# Chain Card

## Role

The single lifecycle control of the main window: the animated chain rail, the status rows, the
launch action row, the compact collapsed form, and the guards card beneath it.

## Key Files

- `src/PrivateNetRouting.App/MainWindow.xaml` + `src/PrivateNetRouting.App/MainWindow.xaml.cs` — the card, status rows, launch row, guards card, `MainViewModel`
- `src/PrivateNetRouting.App/ChainRail.cs` — the horizontal stage rail (markers on links, animations)
- `src/PrivateNetRouting.App/StepRail.cs`, `src/PrivateNetRouting.App/FormattedRuns.cs` — update-window rails and padded mono runs
- `src/PrivateNetRouting.App/LaunchPrompts.cs` — the shared restart-consent dialog

## Behavior

**Rail** (stage keys picked by mode): tailscale 5 stages (Daemon/Keeper/Node/Proxy/Health),
external modes 3 (Egress/Proxy/Health), skipped shapes 2 (Egress/Health — `ProxyRunning` is
null there). Stage words during an operation: Active "starting"/"stopping" (accent, breathing),
Done "ready"/"stopped" (check), Skipped "skipped", Failed "failed" (cross), Pending "waiting".
The `Lock` component shows the wait hint; a `Daemon` step with `NeedsUac` shows the UAC hint;
a `Node` step with a new `AuthUrl` opens the browser once per URL.

**Snapshot protection**: `RefreshChainAsync` bails while an operation is busy; every snapshot
carries an epoch (bumped at operation begin/end) and a monotonic sequence number — a result
whose epoch moved or whose sequence is older than the applied one is dropped. A chain
snapshot taken during an operation never overwrites the rail; a language change repaints from
the last state only when no operation is live.

**Stop-as-cancel**: while a bring-up runs, "Stop" cancels it — the raised links are rolled
back (nothing, if the bring-up was still waiting for another process's lock). While a launch
runs, its cancel rolls back only the links the launch raised. Both tray exits await the same
cancel-and-rollback before their own stop/handover.

**Compact row**: collapsed shows an overall dot + word and the two buttons; a chain operation
expands the card and collapses it back on success; the choice persists in
`ui.chainCardExpanded` (an operation-driven expand does not persist).

**Status rows**: listeners + connection counters (with "—" when the counters live in another
process); the upstream row (daemon listeners + TCP-table state, or the upstream with its user
— never the password — and a reachability word); the config path with open/reload actions
(a reload that changed `forwardProxy`/`chain` or a captured value says "restart applies it");
the idle stopper with its single enabled checkbox (checked starts the stopper over a live chain
and keeps it armed after every raise; unchecked stops it; a chain that is down shows "waiting
for the chain" until the next raise).

**Launch row**: the Claude pair (launch + update with its coral dot) pinned left, the shared
"Browser" button centered (leaning to the freed side when one app is disabled), the Codex pair
(launch in the OpenAI green + update with its green dot) pinned right. `claude.enabled=false`
/ `codex.enabled=false` remove the pair and its tray item. All launches share the
running-copy consent flow (`LaunchPrompts`) and the health-check-first contract.

**Guards card**: four rows (ban / policy / settings / WSL) with per-layer state words and
actions; the collapsed form shows the aggregate (`GuardAggregate`: Unknown dominates, all-on
good, all-off bad, else mixed) plus one combined button — "enable all" (one elevation for ban
+ policy, then settings and WSL sync best-effort; the result line names the failures and keeps
the live ban verdict) / "disable all" (ban + policy, one elevation). Status is polled on a
60 s timer with the same epoch/seq staleness drop. Collapsed state persists in
`ui.guardsCardExpanded`.

## Error Handling

- A failing chain link paints its rail stage Failed (cross) with the component's message; the
  operation slot clears and the card collapses back on success only.
- A failed proxy handover on exit logs + a MessageBox and the exit continues (never blocks).
- A failed config reload is reported on the result line; `ConfigNeedsRestart` persists until the
  file matches the running values.
- A snapshot repaint is dropped when stale (epoch/seq guard) — a slow refresh never clobbers a
  live operation's rail.

## Invariants

- At most one chain/launch operation at a time (shared busy flags and one operation slot).
- Stop during a bring-up cancels; Stop during a stop waits it out.
- A cancelled bring-up restores the pre-operation chain state (rollback of raised links only).
- The rail is never repainted from a stale or mid-operation snapshot.
- Every state color is paired with its word.

## Related Specs

- [App shell](README.md) — the window and tray around the card
- [Chain](../chain/README.md) — what the operations do underneath
- [Guard](../guard/README.md) — the guards card's status model
- [Watchdog](../watchdog/README.md) — the idle stopper row
