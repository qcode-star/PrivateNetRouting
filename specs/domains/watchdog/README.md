# Watchdog

## Purpose

Idle auto-stop: while the chain is up and nothing uses it, stop the app hosts inside the
running configured WSL distros and then the chain itself — so the tailnet node, the proxy and
the WSL app hosts do not linger unattended. The stopper lives only over a live chain and never
registers itself in autostart.

## Key Files

- `src/PrivateNetRouting.Core/Watchdog/WatchdogService.cs` — the idle-stop loop
- `src/PrivateNetRouting.Core/Watchdog/ClaudeUsageProbe.cs` — the activity signals (detail: [Usage probe](usage-probe.md))
- `src/PrivateNetRouting.Core/Watchdog/WslAppHost.cs` — in-distro app-host stopping (detail: [WSL app stoppers](wsl-app-stoppers.md))
- `src/PrivateNetRouting.Core/Watchdog/WatchdogSupervisor.cs` — stopper lifecycle (spawn/find/stop), autoStart hook
- `src/PrivateNetRouting.Core/Watchdog/WatchdogWorker.cs` — the `--watchdog-worker` body, one-stopper rules

## Core Types

```csharp
public interface IClaudeUsageProbe {
    string? GetInUseReason(TimeSpan idleWindow);   // null = idle; fail-closed
}
// WatchdogSupervisor: WorkerMarker = "--watchdog-worker"
//   StartAsync / StopAsync / FindStopperPids / TrackAutoStart / LastAutoStart
```

## Flow

```
RunAsync(loop):
  every watchdog.checkSeconds (first poll after one interval):
    reason = usageProbe.GetInUseReason(checkSeconds)
    busy?  -> Debug log, continue
    idle:
      Info "auto-stop"
      stop app hosts in RUNNING configured distros   (WSL step; skipped when none)
      re-check activity   (only when the WSL step ran — minutes passed)
      still idle -> Chain.StopAsync through the orchestrator
        success -> stopper exits
        failure -> ChainStopFailedException counted;
                   3 consecutive -> give up, rethrow, stopper exits
```

Logging: every tick is Debug; Info carries only a busy-reason change and the auto-stop itself.

**Stopper forms**:
- GUI "Start" -> detached worker (`--watchdog-worker` spawned through `cmd`, stderr to
  `watchdog.err.log`) that outlives the application and switches itself off after the idle
  stop. Never written to autostart.
- `pnr watchdog` -> foreground stopper in the terminal.

**One stopper at a time** (`WatchdogWorker.RefusalReason`): the foreground form refuses beside
any running stopper (exit 0 — a normal no-op); a worker steps aside only for another worker
with a lower pid (never for a foreground stopper — a race leaves the worker); a stopped chain
refuses ("nothing to watch"). Spawn is serialized by `watchdog.spawn.lock` (fail-open).

**Liveness/identity**: read from process command lines (WMI prefilter, then exact argument
splitting): the GUI/worker form by the `--watchdog-worker` marker, `pnr.exe` by `watchdog` as
the first argument (`pnr logs show watchdog` is not a stopper); the executable directory must
be this installation's. Stop kills only re-identified pids (pid-reuse guard), spares the
calling stopper.

**autoStart** (`watchdog.autoStart`, default on): after every successful chain raise the
orchestrator's hook re-arms the stopper fire-and-forget (tracked so a CLI can await it before
exit). The chain-stopped hook takes the stopper down (the calling stopper spares itself and
exits).

## Invariants

- The stopper cannot be armed over a down chain (GUI Start disabled; `pnr watchdog` refuses).
- Exactly one stopper survives any race; the survivor is deterministic (the pid tie-break).
- The WSL pass never boots a distro — only already running ones are visited.
- A completed chain stop takes the stopper down through the chain-stopped hook.
- Nothing here is ever registered in autostart (AGENTS.md rule #4).

## Configuration

| Key | Default | Notes |
|---|---|---|
| `watchdog.checkSeconds` | 600 | poll interval and activity window |
| `watchdog.autoStart` | `true` | re-arm after every successful raise |
| `watchdog.workerStartWaitSeconds` / `workerStopWaitSeconds` | 15 / 10 | worker spawn/stop bounds |
| `wsl.distros`, `wsl.mode`/`syncClaude`/`syncCodex` | — | which distros/hosts the WSL step stops |

## Extension Points

- A new activity signal: add it to `ClaudeUsageProbe.GetInUseReason` in priority order and
  keep the fail-closed rule (a throwing signal means "busy", never "idle").
- A new stoppable host inside a distro: extend `WslAppHostStopper` with its own pass, gated by
  its app-enabled flag.

## Related Specs

- [Usage probe](usage-probe.md) — the busy/idle signals
- [WSL app stoppers](wsl-app-stoppers.md) — the in-distro stop pass
- [Chain orchestration](../chain/orchestration.md) — the stop and the hooks
- [Activity marker](../activity-marker.md) — the freshness signal
