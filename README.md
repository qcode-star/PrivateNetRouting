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
| Forward proxy | Local CONNECT proxy: resolves names over DoH, dials through the userspace sidecar's SOCKS5 upstream, listens on localhost and the WSL gateway, and keeps an activity marker for the watchdog. Runs in-process or as a detached worker that outlives the GUI/command. |
| Chain supervisor | Idempotent bring-up/stop of the userspace tailscaled daemon → keeper → node → forward proxy. On tailscale 1.104.1+ the daemon runs without elevation (`chain.daemonElevation`, default auto); older builds start it through a one-shot elevated copy (UAC). |
| Launchers | Claude Desktop with `--proxy-server`/`--proxy-bypass-list`; a browser with an isolated profile (Firefox `user.js` or Chromium flags) and the application proxy. Both bring the chain up when needed; the bring-up runs live in the chain card of the main window. |
| Watchdog | Idle auto-stop: watches Claude processes, proxy connections and the activity marker; on idle it stops the WSL Claude host and then the chain. Started only explicitly (the toggle in the chain card spawns a detached background worker that keeps working after the application is closed and switches itself off after firing, or run `pnr watchdog` in a terminal). The auto-start flag (on by default) re-arms the stopper after every chain bring-up. |
| Updater | Manual MSIX update of Claude Desktop: version check before bring-up, package URL from Claude's own update feed (asked through the already-live proxy) with the AppX journal as the fallback, multi-threaded chunked download through the proxy, Publisher verification, `Add-AppxPackage`. When no source has a package address, the update window offers to raise the chain and asks the sources again over the live chain. A startup check announces an update only with a concrete offered version and shows the line in the main window, which the update window's own check or a finished installation clears; the window closes itself 30 s after a successful installation, with a visible countdown. |
| Guards | Direct-access control: the managed `egressProxyUrl` policy, a local ban (hosts + one NRPT rule + firewall subnet block), Claude Code env on Windows and in WSL, and the WinINET system proxy. Status is read-only; changes go through an elevated helper. |
| Diagnostics | Read-only state summary (port owners, chain processes, WSL bind, counters, marker, CONNECT roundtrip, guards) in a window of its own; the log list/tail/rotation live in the CLI, the window has an "Open logs" button. |
| Localization | UI string dictionaries with on-the-fly switching (en/ru; `ui.language` in the config); the selector shows a drawn flag mark and the short code (en/ru/system). |

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