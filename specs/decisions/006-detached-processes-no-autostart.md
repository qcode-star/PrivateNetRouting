# ADR-006: Detached Processes, No Services, No Autostart

## Status

Accepted

## Context

The chain must outlive the GUI (the desktop apps keep using it after the window is gone), the
idle stopper must survive the exit that armed it, and daemon/keeper logs must keep flowing —
yet the application is a per-user install that must not leave machine-level traces the user
did not ask for (AGENTS.md rule #4).

## Decision

Every long-lived link is an ordinary per-user process with a lifetime contract of its own:
daemon/keeper/node outlive the app (spawned through `cmd` with redirected file handles), the
forward proxy lives in-process or in a detached `--proxy-worker` (handed over at exit), and
the idle stopper is a detached `--watchdog-worker` that switches itself off after the idle
stop. Nothing is ever registered in autostart — no Run keys, no scheduled tasks, no services —
and the installer writes none either. Background modes start only on an explicit user action
(chain raise + `watchdog.autoStart` re-arm is the one documented automatic arm).

## Consequences

- Positive: zero machine-level footprint; uninstall is a folder delete plus the documented
  guard-off commands; nothing starts at logon that the user did not arm.
- Positive: each link's lifetime is explicit and testable (markers in command lines, lock
  files, stop channels) — see the supervisor specs.
- Negative: the user must raise the chain (or arm the stopper) themselves after a reboot;
  "it just works at boot" is deliberately unavailable.
- Negative: process identity across UAC/elevation needs care (hidden command lines on Win11
  24H2+, pipe-based liveness) and a busy port is never forcibly reclaimed from strangers.

## Alternatives Considered

- **Windows service / Task Scheduler autostart** — rejected by rule #4: machine-level
  registration without explicit per-entry consent is out of scope for this project.
- **Everything in-process (links die with the GUI)** — rejected: the desktop apps must keep
  working with the window closed; the chain is load-bearing after the exit.
- **Tray-resident single process for everything** — rejected: the idle stopper must survive
  even the real exit, and a stop of the app would take the chain down with it.

## Related Specs

- [App shell](../domains/app-shell/README.md), [Listener supervisor](../domains/forward-proxy/listener-supervisor.md),
  [Watchdog](../domains/watchdog/README.md)
