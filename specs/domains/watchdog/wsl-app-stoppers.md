# WSL App Stoppers

## Role

`WslAppHostStopper` — stop the Claude and Codex app hosts inside the running configured WSL
distros. Two callers: the idle auto-stop (both apps enabled in the config, as the first
(minutes-long) step before the chain stop) and the owner's manual stop (exactly one app, from
the window — [App stop](../launchers/app-stop.md)).

## Key Files

- `src/PrivateNetRouting.Core/Watchdog/WslAppHost.cs` — `WslAppHostStopper`, `IWslProcessRunner`/`WslProcessRunner`, `IWslAppHost`, `WslHostState`

## Behavior

1. List running distros (`wsl --list --running --quiet`, 30 s timeout), intersect with the
   configured `wsl.distros` names (case-insensitive, trimmed). Nothing running -> `None`
   (no distro is ever booted for this).
2. Per running distro: one `bash -s` script as root (default 120 s timeout; a stuck distro is
   reported `Stuck` and the next one is still visited). The passes are the caller's choice:
   the idle auto-stop passes the apps enabled in the config, the manual stop exactly one app.
3. **Claude pass**: daemons identified by the ps-args suffix pattern of the Claude RPC server
   (`…/.claude/remote/srv/<hash>/server`), sessions by the `ccd-cli` path pattern. Each daemon
   is stopped by **its own binary via RPC** (`timeout 20 <exe-from-/proc/pid/exe> --stop
   --socket <socket-from-cmdline>`; unverifiable -> SKIP), then SIGTERM (daemon + sessions)
   after a 15 s wait, SIGKILL after 10 s more. Orphan sessions (no daemon) get
   SIGTERM -> SIGKILL directly.
4. **Codex pass**: roots are processes whose `/proc/<pid>/environ` carries `CODEX_HOME` AND
   whose command line has an argument with basename `codex|codex.js|codex-*` plus an argument
   exactly `app-server` (`CODEX_HOME` alone is not a signature — a user may export it in every
   shell). Targets = roots + descendants (parent-map closure), fixed by the first scan;
   the script and its ancestors are excluded. SIGTERM -> 10 s -> SIGKILL.
5. Aggregate state is the worst: `Stuck` > `Stopped` > `None`. The final count covers only the
   passes that ran — a Codex-only stop is `Stopped` while Claude hosts keep running. Output is
   `REMAIN=n` + `STATE=none|stopped|stuck` (a missing STATE parses as `Stuck`).

## Error Handling

- A per-distro timeout marks that distro `Stuck` and continues with the next.
- Caller cancellation propagates (the bash run is killed with the tree).
- A `wsl.exe` listing failure is a run failure (never a silent "nothing running").

## Invariants

- Only already running, configured distros are touched; nothing is booted or shut down.
- Every in-distro script is LF-normalized at the boundary and ends with the `PNR=ok` completion
  marker (wsl.exe exit codes hide bash failures).
- Claude daemons are stopped through their own binary's RPC first; signals are the escalation.
- A process counts as Codex only with BOTH the `CODEX_HOME` environment and the `app-server`
  command-line signature.

## Related Specs

- [Watchdog](README.md) — where this pass runs in the idle loop
- [App stop](../launchers/app-stop.md) — the manual caller, one app at a time
- [Guard WSL layer](../guard/wsl-layer.md) — the distro configuration shared with the sync
- [Running app](../launchers/running-app.md) — the Windows-side counterpart
