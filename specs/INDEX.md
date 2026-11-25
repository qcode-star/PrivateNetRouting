# Specs Index

Navigation: find your task below, read the listed specs before touching code. Formats and
update rules live in [META.md](META.md).

## Task -> Spec

| Task | Read first |
|---|---|
| Change the CONNECT pipeline / refusals / relay | [Forward proxy](domains/forward-proxy/README.md), [Connect proxy](domains/forward-proxy/connect-proxy.md), [Protocols](contracts/protocols.md) |
| Change listener placement, workers, handover | [Listener supervisor](domains/forward-proxy/listener-supervisor.md), [Host <-> Core](contracts/host-core.md) |
| Change DoH resolution / the endpoint pool | [DoH resolution](domains/doh-resolution.md) |
| Change upstream dialing (SOCKS5) | [SOCKS5 client](domains/socks5-client.md), [Protocols](contracts/protocols.md) |
| Change chain bring-up/stop/lock/hooks | [Chain orchestration](domains/chain/orchestration.md), [Chain](domains/chain/README.md) |
| Change the tailscale daemon/keeper/CLI | [Chain sidecar](domains/chain/sidecar.md) |
| Change elevation / the runas helper | [Elevated helper](domains/elevated-helper.md), [Security model](architecture/security-model.md) |
| Change how Claude/Codex start (flags, bypass, restart) | [Launchers](domains/launchers/README.md), [Codex](domains/launchers/codex.md), [Running app](domains/launchers/running-app.md) |
| Change the manual stop of Claude/Codex (Windows + WSL) | [App stop](domains/launchers/app-stop.md), [Running app](domains/launchers/running-app.md), [WSL app stoppers](domains/watchdog/wsl-app-stoppers.md), [ADR-008](decisions/008-manual-app-stop.md) |
| Change the browser profile or its launch | [Browser profile](domains/browser/README.md), [Profile files](domains/browser/profile-files.md), [Launch](domains/browser/launch.md) |
| Change idle auto-stop / the stopper | [Watchdog](domains/watchdog/README.md), [Usage probe](domains/watchdog/usage-probe.md), [WSL app stoppers](domains/watchdog/wsl-app-stoppers.md) |
| Change the Claude MSIX update | [Claude MSIX update](domains/updater/claude-msix.md), [Updater](domains/updater/README.md) |
| Change the Codex Store update | [Codex Store update](domains/updater/codex-store.md) |
| Change the ban / policy / live probes | [Guard](domains/guard/README.md), [Ban layers](domains/guard/ban-layers.md) |
| Change settings.json / config.toml / .env sync | [Settings sync](domains/guard/settings-sync.md), [Proxied apps](contracts/proxied-apps.md) |
| Change the WSL layer (boot, scripts, snippets) | [WSL layer](domains/guard/wsl-layer.md) |
| Change the WinINET system proxy | [System proxy](domains/guard/system-proxy.md) |
| Add/change a config key or the config store | [Configuration](domains/configuration/README.md), [Configuration store](domains/configuration/store.md) |
| Run two configs/profiles side by side | [Profiles](domains/configuration/profiles.md), [ADR-007](decisions/007-profiles.md) |
| Change logging / rotation | [Logging](domains/logging.md) |
| Change diagnostics output | [Diagnostics](domains/diagnostics.md) |
| Change the app self-update | [App update](domains/app-update.md) |
| Change tray / single instance / exits / windows | [App shell](domains/app-shell/README.md) |
| Change the chain card / rail / guards card | [Chain card](domains/app-shell/chain-card.md) |
| Change the update windows / update dot | [Update windows](domains/app-shell/update-windows.md) |
| Add a UI language / change localization | [Localization](domains/app-shell/localization.md) |
| Change a `pnr` verb / exit codes | [CLI](domains/cli.md), [Host <-> Core](contracts/host-core.md) |
| Add a new OS touchpoint (processes, registry, PowerShell…) | [System boundary](contracts/system-boundary.md), [Layers](architecture/layers.md) |
| Understand a cross-cutting design choice | [Decisions](decisions/) (table below) |
| Understand the request/operation lifecycles | [Data flow](architecture/data-flow.md) |
| Understand the guard/elevation/secrets posture | [Security model](architecture/security-model.md) |

## Dependency Graph

```
App shell ----+                         +--> contracts/system-boundary --> Windows
CLI ----------+--> ProxyRuntime --------+
                  |                     +--> contracts/proxied-apps --> Claude/Codex files
                  +-- Net/Proxy --> Net/Dns (DoH) --> Net/Socks (SOCKS5) --> contracts/protocols
                  +-- Chain --> elevated-helper --> (guard commands shared)
                  +-- Launcher --> RunningApp
                  |            --> AppStopper --> Watchdog/wsl-app-stoppers
                  |            --> Browser (profile + launch)
                  +-- Updater (claude-msix | codex-store)
                  +-- Guard (ban | settings-sync | wsl-layer | system-proxy)
                  +-- Watchdog (usage-probe | wsl-app-stoppers)
                  +-- Diagnostics / Logging / ActivityMarker / AppUpdate
                  +-- Configuration (model | store | profiles)
```

## Architecture

- [Layers](architecture/layers.md) — hosts -> Core -> system interfaces; import rules
- [Data flow](architecture/data-flow.md) — CONNECT, chain operation, idle stop, update run
- [Security model](architecture/security-model.md) — ban layers, proxy refusals, elevation, secrets

## Domains

| Domain | Files |
|---|---|
| Forward proxy | [README](domains/forward-proxy/README.md), [Connect proxy](domains/forward-proxy/connect-proxy.md), [Listener supervisor](domains/forward-proxy/listener-supervisor.md) |
| DoH resolution | [doh-resolution.md](domains/doh-resolution.md) |
| SOCKS5 client | [socks5-client.md](domains/socks5-client.md) |
| Activity marker | [activity-marker.md](domains/activity-marker.md) |
| Chain | [README](domains/chain/README.md), [Orchestration](domains/chain/orchestration.md), [Sidecar](domains/chain/sidecar.md) |
| Elevated helper | [elevated-helper.md](domains/elevated-helper.md) |
| Launchers | [README](domains/launchers/README.md), [Codex](domains/launchers/codex.md), [Running app](domains/launchers/running-app.md), [App stop](domains/launchers/app-stop.md) |
| Browser profile | [README](domains/browser/README.md), [Profile files](domains/browser/profile-files.md), [Launch](domains/browser/launch.md) |
| Watchdog | [README](domains/watchdog/README.md), [Usage probe](domains/watchdog/usage-probe.md), [WSL app stoppers](domains/watchdog/wsl-app-stoppers.md) |
| Updater | [README](domains/updater/README.md), [Claude MSIX](domains/updater/claude-msix.md), [Codex Store](domains/updater/codex-store.md) |
| Guard | [README](domains/guard/README.md), [Ban layers](domains/guard/ban-layers.md), [Settings sync](domains/guard/settings-sync.md), [WSL layer](domains/guard/wsl-layer.md), [System proxy](domains/guard/system-proxy.md) |
| Configuration | [README](domains/configuration/README.md), [Store](domains/configuration/store.md), [Profiles](domains/configuration/profiles.md) |
| Logging | [logging.md](domains/logging.md) |
| Diagnostics | [diagnostics.md](domains/diagnostics.md) |
| App self-update | [app-update.md](domains/app-update.md) |
| App shell | [README](domains/app-shell/README.md), [Chain card](domains/app-shell/chain-card.md), [Update windows](domains/app-shell/update-windows.md), [Localization](domains/app-shell/localization.md) |
| CLI | [cli.md](domains/cli.md) |

## Contracts

- [System boundary](contracts/system-boundary.md) — Core <-> Windows interfaces, fakes, privileges
- [Host <-> Core](contracts/host-core.md) — the ProxyRuntime facade and lifecycle/exit contracts
- [Proxied apps](contracts/proxied-apps.md) — maintained shapes of Claude/Codex files and policy
- [Protocols](contracts/protocols.md) — CONNECT / SOCKS5 / DoH wire contracts

## Decisions

Template: [`_template.md`](decisions/_template.md) (copy for a new ADR — see [META.md](META.md#adr-decisionsnnn-slugmd)).

| ADR | Title |
|---|---|
| [001](decisions/001-routing-without-drivers.md) | Routing without drivers or process injection |
| [002](decisions/002-userspace-sidecar.md) | Userspace sidecar with private state |
| [003](decisions/003-forward-proxy-translator.md) | Local forward proxy as the client translator |
| [004](decisions/004-three-layer-ban.md) | Three-layer local ban plus managed policy |
| [005](decisions/005-chain-mode-shapes.md) | Chain mode shapes behind one orchestrator |
| [006](decisions/006-detached-processes-no-autostart.md) | Detached processes, no services, no autostart |
| [007](decisions/007-profiles.md) | Profiles — one process tree per config |
| [008](decisions/008-manual-app-stop.md) | Manual stop of the desktop apps |
