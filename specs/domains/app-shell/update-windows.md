# Update Windows

## Role

The two update windows — `UpdateWindow` (Claude MSIX) and `CodexUpdateWindow` (Store) — that
draw an update run live: step rails, progress, stop/defer, the `NoChange` failure, the update
dot sync, and the 30 s auto-close after a real installation.

## Key Files

- `src/PrivateNetRouting.App/UpdateWindow.xaml` + `src/PrivateNetRouting.App/UpdateWindow.xaml.cs` — the Claude MSIX window (4-step rail)
- `src/PrivateNetRouting.App/CodexUpdateWindow.xaml` + `src/PrivateNetRouting.App/CodexUpdateWindow.xaml.cs` — the Codex Store window (3-step rail)
- `src/PrivateNetRouting.App/StepRail.cs` — `StepRailRow.Apply(ChainStepStatus, detail)` marker vocabulary
- `src/PrivateNetRouting.App/FormattedRuns.cs` — padded mono runs for detail/countdown lines

## Behavior

**Step rails**: UpdateWindow `Check / Chain / Download / Install`; CodexUpdateWindow
`Check / Download / Install`. Markers: Pending hollow, Active accent fill, Done check, Failed
cross, Skipped drawn as Done. The link left of a stage takes the upstream stage's color.

**Progress**: a determinate bar + percentage when byte totals are known (download detail via
`FormattedRuns`: padded done/total/rate/ETA, ~1 s repaint, a 3-minute stall hint), a breathing
indeterminate bar otherwise.

**Stop/close rules**: "Stop" is enabled only in the Chain/Download phases (Codex: it also
cancels the Store item); "Close" is disabled during Install and refused there
(`Update.Install.NoClose`); closing during Chain/Download asks for confirmation. UpdateWindow
adds a pre-install pause when Claude is running (`Install now`/`Defer`; Stop = defer) and the
no-address path (manual URL panel + one offer to raise the chain).

**NoChange**: the install step turns Failed with the `NoChange` detail and the version warning;
the run finishes "failed"; the update dot **stays**.

**Update dot sync** (in the main window): the dot clears when the installed version is up to
date and lights when a concrete offered version exists (Claude) / an update is available
(Codex); the startup checks and the update windows' finished runs both keep it in sync. An
update is announced only with a concrete offered version ("no address known" is not "an update
exists"); the startup announce is an accent dot + a tray balloon.

**Auto-close**: after a real installation the window closes itself in 30 s with a visible
countdown (padded seconds via `FormattedRuns`); any interaction — a click outside a button, a
button press, a key — cancels the countdown and keeps the window open.

## Error Handling

- A failed phase marks its step Failed (cross) with the error detail; the run finishes "failed"
  with Retry offered and the window stays open.
- `NoChange` marks the install step failed with the version warning and keeps the update dot.
- A refused close during Install (`Update.Install.NoClose`) is a message, not a silent no-op.
- A cancelled run cancels the underlying work (Claude download keeps its resume parts; Codex
  cancels the Store item) and marks the step Failed.

## Invariants

- A step never repaints from a stale run (epoch/seq guard like the chain card).
- The dot state always matches the last resolved version verdict.
- The countdown never closes the window under an interacting user.
- Install-phase errors keep the window open with the step marked failed and the retry offered.

## Related Specs

- [Updater](../updater/README.md) — the runs these windows drive
- [App shell](README.md) — the dot and the tray balloon live in the main window/tray
- [Chain card](chain-card.md) — the shared rail vocabulary
