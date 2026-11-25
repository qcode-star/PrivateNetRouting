# Profiles

## Purpose

Two applications need two different chains at the same time: Claude Desktop goes through the
tailscale sidecar, Codex through an external SOCKS5 upstream. A profile is a named config file
plus one process tree of its own; the default profile is the long-standing single config. The
OS process boundary is the isolation: each profile composes its own `ProxyRuntime` (chain,
forward proxy, DoH pool, watchdog, diagnostics) from its own config, so the core stays
single-chain by construction. The machine-wide guard values are the exception: they are
single-valued and have exactly one owning profile.

## Selection

- `--profile <name>` — anywhere among the arguments of the GUI exe, `pnr` and the detached
  workers. No option — the default profile.
- Name: `[a-z0-9][a-z0-9._-]{0,31}`, case-insensitive and canonicalized to lower case (the file
  system is); `default` is reserved.
- Path: default — `%APPDATA%\PrivateNetRouting\appsettings.json`; named —
  `%APPDATA%\PrivateNetRouting\profiles\<name>.json`.
- A named profile must exist: `pnr --profile <name> config init` creates it; a command or GUI
  over a missing named profile is refused (a fresh defaults file would silently collide with
  the running profile's ports and logs). The default profile is still created on first launch.
- Display name: `profile.name` (trimmed) when set, otherwise the typed `--profile` argument; a
  default profile with an empty `profile.name` adds no suffix.

## Identity

`ProfileContext(Name, ConfigPath, Label)` is the resolved selection; every OS-level name is
derived from it:

- GUI single-instance mutex and the Activate/Launch/LaunchBrowser events: the default profile
  keeps `Local\PrivateNetRouting.SingleInstance` and friends; a named profile appends
  `.<name>`. Every GUI additionally holds `Local\PrivateNetRouting.AnyInstance` — the
  installer's `AppMutex`, so an upgrade sees any profile's window.
- Detached workers carry `--profile <name>` in their command line; the watchdog stopper search,
  the proxy worker ownership check and the diagnostics list compare it. A default-profile
  worker carries no option.
- Window title and tray tooltip/balloon: `<product> — <display>`.

## Config path plumbing

`ProxyRuntime.FromConfig(config, logFileName, profile)` stores the profile. Its config path goes
to the elevated helper (`--config`, already trusted by `ElevatedHelperTrust`), to
`ReloadConfig`, to every GUI settings write and to the browser profile persist.
`ProxyWorker` and `WatchdogWorker` load `profile.ConfigPath` (the proxy worker's `--listen`
override still wins).

## Isolation rules

- The second profile's config gives it its own `forwardProxy.listen`, `logging.directory` and
  `forwardProxy.activityMarkerPath`: the chain operation lock and the watchdog spawn lock live
  next to the log, so distinct log directories mean independent operations. The tailscale
  section only matters in the tailscale mode — two tailscale profiles would also need distinct
  `stateDir`, `socket`, `socksListen` and `httpListen`.
- A named profile never adopts a listener belonging to another profile: a busy
  `forwardProxy.listen` whose owner is not this profile's worker is an explicit error rather
  than a silent route through the wrong chain. The default profile keeps the long-standing
  handover behaviour (a worker of a previous run).
- `claude.enabled`/`codex.enabled` decide whose settings files are synced;
  `wsl.syncClaude`/`wsl.syncCodex` split the WSL halves of the layer; the idle probe counts only
  the enabled apps, so a running Claude does not keep the Codex profile's chain alive (and vice
  versa).
- The ban stays shared and single: one set of identifiers, the same lists in both profiles.
  Either profile may enable or disable it for the machine.

## Guard ownership

`HKLM\SOFTWARE\Policies\Claude` and the per-user WinINET switch are single-valued, so exactly one
profile owns them:

- `guard.managePolicy` (default true) — false: the layer is read for display, never judged,
  never voted in the card, and `policy on|off` (GUI, CLI, the elevated helper's combined
  `all on|off`) skips it.
- `guard.manageSystemProxy` (default true) — false: the WinINET switch is displayed as not
  managed and `pnr guard sysproxy on|off` refuses.

The owner is the profile whose app the value names: the Claude profile owns the Claude policy;
the system proxy belongs to whichever profile the owner wants WinINET applications to use.

## Key Files

- `src/PrivateNetRouting.Core/Configuration/AppProfile.cs` — option parsing/validation,
  path resolution, `ProfileContext`
- `src/PrivateNetRouting.Core/Configuration/AppConfig.cs` — `profile.name`,
  `guard.managePolicy`/`manageSystemProxy`
- `src/PrivateNetRouting.App/App.xaml.cs` — one GUI per profile (mutex/events/AnyInstance)
- `src/PrivateNetRouting.Cli/Program.cs` — the global option, the profile config everywhere
- `src/PrivateNetRouting.Core/ProxyRuntime.cs` — the profile carried by the runtime

## Related Specs

- [Configuration](README.md) — the model and defaults
- [Configuration store](store.md) — load/save/update semantics per path
- [App shell](../app-shell/README.md) — single instance and the window/tray identity
- [Guard](../guard/README.md) — the single-owner flags
- [Watchdog](../watchdog/README.md) — the profile-scoped stopper
- [ADR-007](../../decisions/007-profiles.md)
