# ADR-007: Profiles — One Process Tree per Config

## Status

Accepted

## Context

Claude Desktop must reach the network through the tailscale sidecar while Codex goes through an
external SOCKS5 upstream, both at the same time. The application had one config file, one config
path in both hosts and both workers, one GUI mutex and one watchdog/worker identity; switching
the chain meant copying a config over the single file and restarting everything. The core is
single-chain by construction (`ProxyRuntime` owns one `ChainSupervisor`, one
`ForwardProxyService`, one `DohEndpointPool`), and several guard values are physically
single-valued on the machine (`HKLM\SOFTWARE\Policies\Claude`, the per-user WinINET switch, one
hosts block/NRPT rule/firewall group).

## Decision

A **profile** is a named config file plus one process tree:

- `--profile <name>` selects `%APPDATA%\PrivateNetRouting\profiles\<name>.json`; no option is the
  long-standing default config (`appsettings.json`). A named profile must exist (created
  explicitly by `config init`) — a fresh defaults file would collide with the running profile.
- The profile is the identity of every OS-level name: the GUI mutex and its events, the detached
  workers' `--profile` argument, the watchdog stopper search and the proxy worker ownership
  check. Every GUI also holds a profile-agnostic `AnyInstance` marker for the installer.
- `ProxyRuntime` carries the profile (config path + identity); the path reaches the elevated
  helper, the reload and every settings write.
- Two processes, not two chains in one: the OS is the isolation boundary and the single-chain
  core stays untouched. The second profile's config gives it its own proxy port, log directory,
  activity marker and WSL half.
- Machine-wide values keep one owner per profile: `guard.managePolicy` and
  `guard.manageSystemProxy` (default true) let the other profile read them without ever
  judging or writing them. The ban stays one shared block with shared identifiers.

## Consequences

- Positive: both applications work simultaneously through different chains with no change to the
  chain/proxy/DoH design; each profile has its own logs, locks, watchdog and tray/window title.
- Positive: the elevated helper, the settings sync and the WSL layer keep their existing shapes —
  the profile is just a config path and a name.
- Negative: the second profile's config must be authored carefully (distinct port/log/marker);
  the application detects a foreign listener on the proxy port but cannot guess a free one.
- Negative: `HKLM\SOFTWARE\Policies\Claude` and WinINET remain single-valued — one profile owns
  them, the other reports "not managed by this profile".
- Negative: the ban is shared; either profile can turn it off for the whole machine.
- Negative: the installer upgrade stops all profiles' workers and needs every GUI closed (the
  `AnyInstance` marker covers them), then the chains are raised again.

## Alternatives Considered

- **Two chains inside one process** — rejected: `ProxyRuntime`, the chain supervisor, the proxy,
  the DoH pool, the guard and diagnostics are single-instance by construction; the refactor is
  large and buys nothing the process boundary does not.
- **Separate Windows users / sessions per profile** — rejected: the guard and the tailnet node
  are per-user/machine, the desktop apps would be launched across sessions, and the setup is
  disproportionate.
- **Copy the binary into two install directories** — rejected: it duplicates the product, breaks
  the per-user install and does not solve the shared guard values.
