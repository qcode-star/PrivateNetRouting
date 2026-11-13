# Sidecar

## Role

The tailscale-mode internals: the userspace tailscaled daemon lifecycle (direct or elevated),
the keeper that keeps the engine alive, and the CLI wrapper the node is driven through.

## Key Files

- `src/PrivateNetRouting.Core/Chain/SidecarDaemon.cs` — `SidecarDaemon` (start/kill, stale handling, pipe liveness)
- `src/PrivateNetRouting.Core/Chain/SidecarKeeper.cs` — `SidecarKeeper` (watch-ipn bus subscriber)
- `src/PrivateNetRouting.Core/Chain/SidecarCli.cs` — `SidecarCli`, `AuthUrlExtractor`, `CliResult`
- `src/PrivateNetRouting.Core/Chain/TailscaleVersionProbe.cs` — `tailscaled --version` parsing (5 s limit)
- `src/PrivateNetRouting.Core/Chain/SystemControlUrlFinder.cs` — the system daemon's `ControlURL`

## Behavior

**Daemon start** — command line (every config value passes `CommandLineSafety` inertness/pipe
checks first): `tailscaled --tun=userspace-networking [--windows-mode=dev] --statedir=…
--socket=… --port=0 --socks5-server=<socksListen> --outbound-http-proxy-listen=<httpListen>`,
launched through `cmd` with `daemon.out.log`/`daemon.err.log` in the statedir.

**Elevation decision** (`chain.tailscale.elevation`): `never`/`always` force a mode; `auto`
(the default) probes the binary version — below `1.104.1` (or unknown) elevates, because that
release is where the named pipe stopped being Administrators-owned. The direct path always
passes `--windows-mode=dev`; the elevated path derives the dev flag from the version of the
binary it actually runs (1.104.1+ gets it even elevated). The elevated start pins the
**standard per-machine install** regardless of `tailscaledPath`/`cliPath` (the user-writable
config does not name what runs as administrator) and refuses without it; the elevated stop
downs the node with the standard CLI when present, then kills by image name + statedir/socket.

**Stale handling / liveness**: alive-without-listener -> kill + restart (counts as raised);
listener-without-pipe -> refuse ("port already held by a foreign process"). `IsProcessAlive`
is a named-pipe connect on the configured `--socket` path (an elevated daemon hides its
CommandLine from non-elevated WMI on Windows 11 24H2+, but only our daemon holds our pipe).
Direct kill: match `tailscaled.exe` processes by the statedir whole-argument value, re-check
under a pinned handle before killing (PID-reuse safe), wait 3 s for the pipe to die; a
surviving daemon suggests a previous elevated run (the stop then goes elevated).

**Keeper**: one permanent `tailscale.exe --socket=<pipe> debug watch-ipn` subscriber (the
Windows userspace engine shuts down when the last local ipn client leaves). Started through
`cmd`; identified by the whole `--socket=` argument plus `watch-ipn` in the command line;
killed by the same identification. `EnsureStartedAsync` waits `keeperCheckSeconds` and throws
if it exited immediately.

**CLI** (`SidecarCli`): every call is `tailscale --socket <pipe> <args>` with hard wrapper
timeouts (`cliTimeoutSeconds` for status/down, `upTimeoutSeconds` + margin for up — its own
`--timeout` fires first —, `loginWaitSeconds` for login); on the wrapper's own timeout the
tree is killed and `IOException` is thrown; caller cancellation still surfaces as OCE.
`up` flags: `--accept-dns=false --accept-routes --timeout=<n>s [--login-server=…]`.
The login pumps stdout+stderr lines through `AuthUrlExtractor`: a URL is taken only **after**
a "To authenticate" hint, once per hint (a repeated hint re-arms it), announced via
`onAuthUrl` and returned as `CliResult.AuthUrl`.

**Control server selection**: login server = configured `loginServer`, else the system
daemon's `ControlURL` (`SystemControlUrlFinder`: the CLI without `--socket`, read-only prefs
parse; null is a normal case). The `up` control URL = configured, else the node's own
`ControlURL`, else the system one; null -> default server (no flag).

## Error Handling

- A listener held by a foreign process refuses the start (never killed).
- CLI wrapper timeout -> `IOException` naming the deadline; the child tree is killed first.
- A daemon surviving the direct kill -> `IOException` suggesting an elevated stop.
- The elevated stop verifies via pipe + statedir search (3 s) and throws "the daemon is still
  there after the stop" otherwise; a missing standard CLI skips the `down` with a warning and
  never refuses the stop.

## Invariants

- Only processes carrying our statedir/pipe in their command line are treated as ours.
- The elevated path executes only the standard per-machine install binaries.
- A stop of an already stopped chain asks no UAC (the pipe decides before any elevation).
- The keeper is one process per chain; `KillAsync` waits for its death and fails on a survivor.
- The auth URL is announced at most once per authentication hint.

## Related Specs

- [Chain](README.md) — where these steps sit in the bring-up
- [Orchestration](orchestration.md) — who calls `EnsureStartedAsync`/`KillAsync`
- [Elevated helper](../elevated-helper.md) — the runas commands used here
- [Diagnostics](../diagnostics.md) — reads the same pipe/liveness facts
