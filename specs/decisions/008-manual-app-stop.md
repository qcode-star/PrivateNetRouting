# ADR-008: Manual stop of the desktop apps

## Status

Accepted

## Context

The idle stopper already stops the Claude and Codex hosts inside the running configured WSL
distros and then the chain, and the launch/restart flow closes a running copy that works
without our proxy. Neither gives the owner a way to shut an app down on demand: the launch
flow only closes a copy as part of a launch, only when it carries no proxy of ours, and the WSL
pass belongs to the idle watchdog (it exists only over a live chain and fires after the idle
window). The owner asked for an explicit stop of the Claude and Codex processes on Windows and
in WSL.

## Decision

A manual stop is a separate owner action, one per app ("Stop Claude" / "Stop Codex"), shown as
quiet ghost buttons: a row of their own holding the outer edges under the launch pairs when both
apps are enabled (Stop Claude under Launch Claude, Stop Codex under Launch Codex), next to the
pair when only one is (the missing group leaves the room). It stops the app's own processes
only:

- Windows: the launcher's `RunningApp` machinery (the MSIX install-path/alias markers), WM_CLOSE
  to every window, a bounded wait, a tree kill of the survivors — a copy running with our proxy
  included;
- WSL: the idle stopper's script with exactly that app's pass enabled, only over distros that
  are already running;
- the chain is not touched; no confirmation dialog (the click is the owner's decision); the
  launch button never changes meaning, and a launch waits while a stop runs.

## Consequences

- The "a running copy with our proxy is never closed" invariant now belongs to the launch and
  restart flows; the manual stop deliberately closes such a copy.
- `WslAppHostStopper` has two callers (the idle watchdog, the manual stopper) and the app
  selection is per call, not baked in at composition.
- The desktop apps are machine-wide while the profiles are per config: a stop from one profile's
  window closes the app for every profile. That is the point of the action (the owner asked for
  the app to be stopped) and is documented in the launchers spec.
- The window gains no live "running" indicator: the stop is always offered while the app is
  enabled and reports what it found, "nothing to stop" included.

## Alternatives Considered

- Launch button morphing into Stop: the running state is not known to the UI and would need a
  continuous WMI scan plus a root `/proc` scan per running distro; the label would lie exactly
  for WSL-only leftovers; a launch of a running copy still has a function (a single-instance app
  is activated); a destructive action in the place of the habitual one invites misclicks.
- Killing by image name: would take the `claude.exe` native CLI, a manual codex CLI or an
  unrelated process that shares the name; the marker filters exist to prevent exactly that.
- Stopping only through the restart flow: needs a launch and refuses a copy that already carries
  our proxy — no way to simply wind an app down.
- A CLI-only verb: discoverable only to the owner who remembers it; the requested control is a
  button in the window.
