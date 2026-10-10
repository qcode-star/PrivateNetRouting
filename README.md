# PrivateNetRouting

A personal Windows toolkit (WPF, .NET 10 LTS) that runs Claude Desktop through a private proxy
chain: a local forward proxy with DoH resolution, a chain supervisor, launchers for Claude Desktop
and for an isolated browser profile, idle auto-stop, an MSIX package updater, direct-access guards,
and read-only diagnostics. It provides a GUI and a headless CLI. Development rules are in
[AGENTS.md](AGENTS.md).

## Install

Releases live on the [Releases page](https://github.com/qcode-star/PrivateNetRouting/releases/latest).
Every release carries two self-contained artifacts for Windows x64 (the .NET runtime is bundled —
nothing else to install):

- `PrivateNetRouting-<version>-setup-win-x64.exe` — the installer: a per-user install without
  elevation into `%LOCALAPPDATA%\Programs\PrivateNetRouting`, with start-menu shortcuts and an
  uninstaller. The build is not signed, so SmartScreen asks to confirm the first run.
- `PrivateNetRouting-<version>-win-x64.zip` — portable: unpack anywhere and run
  `PrivateNetRouting.App.exe` (GUI with a tray icon) or `pnr.exe` (CLI).

On first launch the user config is created at `%APPDATA%\PrivateNetRouting\appsettings.json` from
default values. The defaults are placeholders by design (rule #3 in AGENTS.md): endpoints, paths,
bypass domain lists and the like must be filled in for the specific environment — the schema with
comments is in [config/appsettings.example.json](config/appsettings.example.json). Logs are in
`%LOCALAPPDATA%\PrivateNetRouting\logs\`.

## Modules

| Module | What it does |
|---|---|
| Forward proxy | Local CONNECT proxy. In the tailscale mode it resolves names over DoH (the resolvers are picked up from the tailnet netmap — http and https, by IP or by name; the optional `dohSeed` adds extras) and dials by IP through the configured SOCKS5 upstream (the sidecar's listener, or an external proxy — optionally with username/password, RFC 1929). In the socks5 mode the trusted upstream resolves on its side: the target name rides the CONNECT, no DoH involved. Listens on localhost and the WSL gateway, keeps an activity marker for the watchdog. Runs in-process or as a detached worker that outlives the GUI/command. |
| Chain supervisor | Two modes by `chain.mode` (a change applies after a restart). "tailscale" (default): idempotent bring-up/stop of the userspace tailscaled daemon → keeper → node → forward proxy; on tailscale 1.104.1+ the daemon runs without elevation (`chain.daemonElevation`, default auto), older builds start it through a one-shot elevated copy (UAC). "socks5": the chain is one check of the external SOCKS5 upstream from `forwardProxy.socks5Upstream` (a CONNECT-by-name roundtrip through it — the upstream resolves the name on its side) plus the forward proxy itself — no tailscale processes and no UAC; stopping takes down only the proxy. The chain card draws the mode's steps: three tailscale stages become one "Egress" stage. While a bring-up is running (its own, or the one inside a launch), the card's Stop cancels it: what got raised is stopped, a chain raised before the launch keeps working. |
| Launchers | Claude Desktop with `--proxy-server`/`--proxy-bypass-list`; a browser with an isolated profile (Firefox `user.js` or Chromium flags) and the application proxy. Both bring the chain up when needed; the bring-up runs live in the chain card of the main window. |
| Watchdog | Idle auto-stop: watches Claude processes, proxy connections and the activity marker; on idle it stops the WSL Claude host and then the chain. Lives only over a live chain: every completed chain stop takes the stopper down with it, and it cannot be armed over a stopped chain (the GUI Start is disabled, `pnr watchdog` refuses). Started explicitly (the toggle in the chain card spawns a detached background worker that keeps working after the application is closed and switches itself off after firing, or run `pnr watchdog` in a terminal). The auto-start flag (on by default) re-arms the stopper after every chain bring-up. |
| Updater | Manual MSIX update of Claude Desktop: version check before bring-up, package URL from Claude's own update feed (asked through the already-live proxy) with the AppX journal as the fallback, multi-threaded chunked download through the proxy, Publisher verification, `Add-AppxPackage`. When no source has a package address, the update window offers to raise the chain and asks the sources again over the live chain. A startup check announces an update only with a concrete offered version and lights an accent dot on the update button (the versions in its tooltip), which the update window's own check or a finished installation clears; the window closes itself 30 s after a successful installation, with a visible countdown. |
| Guards | Direct-access control: the managed `egressProxyUrl` policy, a local ban (hosts + one NRPT rule + firewall subnet block), Claude Code env on Windows and in WSL, and the WinINET system proxy. Status is read-only; changes go through an elevated helper. |
| Diagnostics | Read-only state summary (port owners, chain processes — or the socks5 egress line, WSL bind, counters, marker, CONNECT roundtrip, guards) in a window of its own; the log list/tail/rotation live in the CLI, the window has an "Open logs" button. |
| Localization | UI string dictionaries with on-the-fly switching (en/ru; `ui.language` in the config); the selector shows a drawn flag mark and the short code (en/ru), with the config's `auto` displayed as the language it resolves to. |

## Build

```bash
dotnet build              # or the full path if PATH is stale in the session:
                          # "/c/Program Files/dotnet/dotnet.exe" build
dotnet test
dotnet run --project src/PrivateNetRouting.App
```

## Run

The WPF application (`PrivateNetRouting.App`) shows a status/control window with a tray icon.
The tray menu shows the window, runs chain up / chain down / launch Claude (the same paths as the
window's buttons) and exits: "Exit" leaves the chain working in the background (the next start sees
it), "Exit with full chain stop" brings it down first. Closing or minimizing the window only hides
it to the tray.
Special modes: `--launch` (the Claude desktop shortcut — show the window if Claude is running,
otherwise launch it, with the chain bring-up drawn live in the chain card) and `--launch-browser`
(the isolated browser profile).

The headless CLI (`pnr`):

```bash
pnr proxy [--detached]         # forward proxy in the console, or as a detached worker
pnr chain status|up|down       # chain state / bring up / stop
pnr check                      # CONNECT roundtrip health check
pnr status                     # read-only diagnostics summary
pnr logs list|show|rotate      # log viewing and rotation
pnr launch [--check-only]      # start Claude Desktop through the proxy
pnr browser [--private] [--url <url>]
pnr update [--check] ...       # manual MSIX update
pnr watchdog                   # idle auto-stop supervisor
pnr guard status [--live]      # guards: read-only status; ban/policy/settings/wsl/sysproxy to change
pnr config path|init           # user config path / create from defaults
```

## Configuration

Everything environment-specific (endpoint addresses, ports, bypass domain lists, paths, timeouts)
lives in the application's user config and never reaches git. The repository holds only templates
with placeholders in `config/*.example.json`. The repository is public and anonymous: no real
addresses, domains, names, or secrets (rule #2 in AGENTS.md).

## License

[Unlicense](LICENSE) — public domain: use, modify and distribute without any restrictions, in open
and closed projects alike, no attribution required.