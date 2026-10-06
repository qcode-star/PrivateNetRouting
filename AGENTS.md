# AGENTS.md

Private personal project: a WPF application on .NET 10 LTS (Windows Desktop) that brings under a
single window all the previously scripted functionality for maintaining Claude Desktop on this
machine: a local proxy with DoH resolution, a proxy chain supervisor, a launcher, idle auto-stop,
an MSIX updater, direct-access blocking, and diagnostics. Below are the rules that must not be
broken and the plan we build by.

## Rule #0: ambiguities — ask the owner

For any ambiguity, imprecision or "unclear point" in the requirements — how the code should behave,
what the defaults are, what counts as an error — do not silently invent an answer; clarify the
desired behavior with the user. One short question before the code is cheaper than reworking it
after.

## Rule #1: repository language — English

Everything in the repository is in English: code, identifiers, comments in any file (C#, XAML,
csproj, JSON configs), md files, config templates, and commit messages. The only exceptions are the
UI localization dictionaries (`src/PrivateNetRouting.App/Localization/Strings.<code>.xaml`), each of
which is in its own language, and the local, non-committed `.secret.agents.md`. No other language may
leak into the rest of the repository.

## Rule #2: repository anonymity — check every commit

The repository is private and must stay **as anonymous as possible**: it does not contain and must
never receive my settings, secrets, or anything that links the machine or the author to the
employer, the corporate network, or its infrastructure. It is forbidden to commit:

- real endpoint addresses and ports: tailnet IPs of nodes, public exit IPs, DoH endpoints,
  DNS server addresses, the tailnet coordination server address;
- corporate domains and zone lists (including the proxy bypass list — it exists only in the config);
- computer and network node names, the Windows user name, WSL distro names and their users,
  paths containing the user name (write `%USERPROFILE%\…` or `~`);
- node keys, tokens, passwords, the contents of `settings.json` of Claude/CLI tools;
- logs and config fragments containing any of the above, and screenshots with them.

Everything environment-specific lives in the application's user config (`%APPDATA%`, not committed,
see rule #3). The repository holds only templates with placeholders (`config/*.example.json`).
The git identity is set locally in the repository: GitHub login + noreply address; do not touch the
global one.

Before every commit, review the diff: IP addresses (only `127.0.0.1`, `0.0.0.0`, `*.example`
samples, and documented public constants such as API hosts in templates are allowed), unfamiliar
domains, node names, secret-like strings. In doubt — do not commit, ask.

**Source documents outside the repository** (the old project `%USERPROFILE%\ZCodeProject`, the
scripts in `C:\ovpn-script`, machine logs) contain real addresses and identifiers — they must not be
copied into the repository verbatim. Carry over only the sanitized essence: mechanics, protocols,
sequence of actions — without addresses, names, or domains.

**Machine-specific additional instructions** (real addresses, names, configurations, references to
sources) live in the local file `.secret.agents.md` in the repository root: it is excluded by
`.gitignore` and must never end up in a commit — this is the deliberate single exception to the ban
on environment-specific values; everything else is as above.

## Rule #3: secrets and configuration

- The application reads the user config from `%APPDATA%\<Application>\` (created on first launch
  from the template). The repository example is `config/appsettings.example.json` with placeholders.
- Nothing environment-specific is hardcoded in the code: addresses, ports, paths to binaries
  (tailscale, wsl, browsers), the WSL distro and user name, the health-check target, bypass domain
  lists, timeouts, idle thresholds.
- Do not store secrets in the config at all (the scheme has no authentication); if they ever appear —
  only Windows Credential Manager / DPAPI, never a plaintext file.

## Rule #4: the old script scheme — the production setup until parity

The working setup is the PowerShell/Python set in `C:\ovpn-script` (launcher, supervisor, ts-fwd
proxy, sidecar chain boot/stop, ban, NO_PROXY sync, status). Until the application replaces the
functionality completely:

- **do not break the working set**: edits to the old scripts only on an explicit request;
- new functionality first lives in the application in parallel (its own ports/paths from the config —
  or a config switch); replacing a link of the old scheme is a deliberate decision by the owner,
  not a side effect;
- the details of how the legacy scheme works are in the old project's docs
  (`%USERPROFILE%\ZCodeProject\docs`); remember rule #2 when carrying knowledge over from there.

## Rule #5: nothing in autostart without explicit confirmation

Do not register or add anything to autostart on your own initiative (scheduled tasks, services,
autostart keys, background processes that outlive the session) — even if the feature is ready and
the benefit is obvious. First show what exactly will be created and where, wait for a "yes". The
same applies to removing existing autostart entries. This is a legacy of the production machine and
extends to the application: background modes (tray, watchdog) — only on an explicit user action.

## Rule #6: no drivers and no injection

Do not use and do not propose tools with drivers or process injection (Proxifier-like tools, WFP
interception, DLL injection). All routing is solved by the proxy and the application's policies.
This is a fundamental limitation of the scheme, not a shortcoming.

## Rule #7: routes, DNS, hosts, firewall — state before, live check after

Before changes to routes/DNS/hosts/NRPT/firewall, look at the current state first; afterwards, verify
the result on live traffic, not "it should work". Separately remember: the application has layers of
protection against direct access (managed policy, hosts, NRPT, firewall) — change them only through
the application's modules, with a transparent status of what was done.

## Rule #8: project md files — current state only

In all md files of the repository we record only the current actual state: no history of "it was
like this, then we changed it", no check dates, no session references. When a fact stops being true
it is rewritten or deleted. AGENTS.md is a living document: a module is implemented — update the
row, don't append a note.

## Target architecture

A single WPF application (single instance, tray + windows) plus a headless mode for CLI parity with
the old scripts. Core modules do not depend on the UI (testability); the UI is a thin wrapper.
Application modes (the owner's decision):

- **normal GUI** (`PrivateNetRouting.App` with no arguments): the status/control window; the tray
  icon lives as long as the application runs; minimizing and closing the window hide it to the tray
  (the proxy and an enabled watchdog keep working); the real exit is "Exit" in the tray menu or
  ending the Windows session; a second launch shows the hidden window;
- **shortcut GUI** (`--launch`): this is how the Claude desktop shortcut works — if Claude is
  already running, the application window is simply shown; if not, a window with the launch process
  (bring-up splash screen: tunnel, checks) and the start of Claude with proxy flags;
- **console** (`pnr`, a separate exe): all headless-parity commands with the old scripts.

Ports per the owner's decision: **18081** (daemon HTTP), **18082** (forward proxy, the address for
the client), **18083** (daemon SOCKS5); the defaults do not conflict with the legacy scheme —
replacing it only by explicitly editing the user config (rule #4).
Porting proceeds module by module from the production set; every module in the application
reproduces the semantics of its source (including edge cases), but takes addresses/ports/names from
the config.

| Module | What it does | Status | Porting source (`C:\ovpn-script`) |
|---|---|---|---|
| ForwardProxy | local CONNECT proxy: DoH resolution of names through the resolvers from the config, dialing through the SOCKS5 upstream of the userspace sidecar, bind on localhost and the WSL gateway, activity marker (mtime) for the watchdog; the listener placement mode is decided by ForwardProxySupervisor: in-process (default) or a detached worker (`forwardProxy.detached`, its own exe with the `--proxy-worker` marker, spawned through cmd with redirection) — the port is a single resource: a busy one is not bound by anyone, stop kills only the worker identified by cmdline, a foreign owner is left alone (warning to the log) | **ready** (`PrivateNetRouting.Core.Net.*`; the detached mode has been checked live: worker bring-up, localhost + WSL gateway listeners, identification by the marker, CONNECT roundtrip, kill with port release, worker fallback to a per-process log) | `ts-fwd.py` |
| ChainSupervisor | idempotent chain bring-up/shutdown (userspace tailscaled daemon → watch-ipn keeper → up → forward proxy → health check), elevation of the daemon start through a runas copy of itself (the only UAC), "our" processes are identified by the statedir/pipe in the command line; daemon/keeper logs are file handles through cmd, surviving application exit. Daemon liveness is determined by the responsiveness of its named pipe: WMI hides the CommandLine of an elevated process from a non-elevated reader (Windows 11 24H2+), while an elevated stop still kills processes by cmdline search. **Checked live with a new statedir**: UAC start of the daemon → keeper → node login through the login server (a blocking `tailscale login`, authentication URL by the owner) → health CONNECT roundtrip; repeated bring-ups are idempotent, the node logs in silently by key; the full cycle "bring-up → auto-stop → bring-up" passed without a second login | **ready** (`PrivateNetRouting.Core.Chain`) | `ts-sidecar-boot/start/stop/cli.ps1`, `ts-sidecar-elevate.vbs`, `ts-sidecar-login.ps1` |
| Launcher | starting Claude Desktop with `--proxy-server`/`--proxy-bypass-list` (the bypass list is built from the config domains, both spellings of each zone; the base entries — localhost/private ranges — are fixed in the code), checking and bringing up the chain (live CONNECT, then an idempotent chain up), a splash screen with the bring-up steps (on a live chain there is no window), control warnings to the log about the `egressProxyUrl` managed policy and an already running Claude without flags, mode flags (check-only/no-splash); the GUI shortcut mode `--launch`: Claude already running (exact process name) — only the application window, not running — a full launch with the splash screen | **ready** (`PrivateNetRouting.Core.Launcher` + `SplashWindow` in App; CLI `pnr launch [--check-only] [--no-splash]`; the splash screen is GUI-only; the Claude desktop shortcut → `PrivateNetRouting.App --launch`). A live launch of Claude has not been checked. By default the proxy lives in the command process — only the detached mode can outlive its exit (`forwardProxy.detached`, disabled by default) | `claude-launcher.ps1`, `claude-splash.ps1`, `run-hidden.vbs`, `ps-host.ps1` |
| Watchdog | idle auto-stop: periodic activity check (claude* processes, connections to the proxy, activity marker freshness; the first poll is after the interval, every timer tick is Debug, Info only carries a reason change and the auto-stop itself), when idle — graceful stop of the WSL Claude host (RPC server.shutdown with its own binary, SIGTERM→SIGKILL escalation on orphans, the distro is not brought up just for the check), then stopping the chain with the existing orchestrator; the start is only explicit (`pnr watchdog` / the checkbox in the application window), it lives as long as the process that started it, and is never written to autostart; the manual WSL host watchdog by mtime (`claude-wsl-watch.ps1`) was not ported | **ready** (`PrivateNetRouting.Core.Watchdog`; the interval and the WSL distro are in the config under `watchdog.*`). The live auto-stop has been checked: idle → WSL host (no-op when the host is absent) → down → UAC stop of the daemon → keeper → exit | `ts-sidecar-watch-claude.ps1`, `claude-wsl-stop.ps1`, `claude-wsl-watch.ps1` |
| Updater | manual MSIX update: version check BEFORE the chain bring-up ("nothing to install" — no UAC), the package address from the AppX journal (or manual entry), multi-threaded chunked download through the application's own forward proxy, Publisher verification, closing Claude, `Add-AppxPackage`; a GUI with steps/progress/stop button, a pause before installation and a deferred installation (`--install-from`); during the download the activity marker is kept fresh (auto-stop does not shut the chain down under the download); on GUI start — a background update check (read-only on versions and the AppX journal, no chain and no UAC; `ui.checkUpdatesOnStart`): if there is one — a line next to the launch buttons and a tray balloon | **implemented** (`PrivateNetRouting.Core.Updater`; CLI `pnr update [--check] [--download-only] [--force] [--keep-file] [--keep-running] [--url <url>] [--install-from <path>]`; the `UpdateWindow` window is part of the GUI application). Only `--check` has been checked live (the version and the journal are read, a missing URL is handled); the download and installation have not been performed live | spec `%USERPROFILE%\ZCodeProject\claude-update-gui-tz.md` + the `claude-desktop-update` skill |
| Guard | direct-access control: the managed policy `egressProxyUrl` (+`disableWslSessions=0`); a local ban in three layers — a hosts block, one NRPT rule for all subtrees, a firewall outbound Block by subnet (the identifiers by default match the legacy scheme — the application maintains the ban already in place instead of creating a parallel one); env in Claude Code settings.json (Windows and WSL: the application proxy + NO_PROXY from the config domains; in WSL also a startup script with the NAT gateway address; the layer is entirely optional: `guard.wsl.mode` = on/off/auto and a per-distro choice of syncing); the WinINET system proxy on/off. The status of all guards is read-only (a GUI card, `pnr guard status [--live]`); the ban and the policy go through an elevated helper, and after a ban — live probes (DNS/direct/proxied) | **implemented** (`PrivateNetRouting.Core.Guard`; CLI `pnr guard status [--live] \| ban on\|off \| policy on\|off \| settings sync \| wsl sync \| sysproxy on\|off`; the "Guards" card in the main window). The status and the live probes have been checked live (read-only: the legacy scheme's ban is visible, the WSL config is readable); the modifying operations (ban/policy/sync) have not been performed live | `set-egress-proxy.ps1`, `ban-ai-hosts.ps1`, `proxy-on/off.ps1`, `sync-claude-noproxy.ps1` + the startup script `/etc/wsl.d/startup/10-claude-proxy.sh` |
| BrowserLauncher | launching a browser with an isolated profile and the application proxy, without touching system settings: Firefox — a separate profile with user.js, rewritten on every launch (the proxy address from `forwardProxy.listen`, `no_proxies_on` — the `launcher.bypassDomains` domains in both forms + `*.tld` from `browser.directTlds`, `failover_direct=false`, `trr.mode=5`), started with `--new-instance` (otherwise the URL would go to the everyday instance without a proxy); Chromium-like — `--proxy-server`/`--proxy-bypass-list`/`--user-data-dir`/`--no-first-run` (a separate directory is mandatory: a running instance ignores the flags); the chain is like the Claude launcher's (live CONNECT, on failure an idempotent bring-up, splash screen while waiting); the browser is chosen by `browser.type` (firefox/chromium, an unknown value — firefox), the private window — `browser.private` or a flag; an already running instance of ours (cmdline with our profile) is not duplicated — a note in the application window | **ready** (`PrivateNetRouting.Core.Launcher.BrowserLauncher`; CLI `pnr browser [--check-only] [--prepare-only] [--private] [--url <url>]`; the "Browser" button in the main window; the GUI shortcut mode `--launch-browser`). Checked live: `--check-only`/`--prepare-only` (user.js is correct), `--launch-browser` with the legacy scheme's profile — claude.ai opens through the proxy, the cmdline of the live instance carries the profile (the "already running" detection works); the "Claude (browser)" shortcuts have been switched to the application. The proxy lives in the command/GUI process unless the detached mode is enabled (`forwardProxy.detached`) | `browser-proxy.ps1` |
| Diagnostics | a read-only state summary (a port of `proxy-status.ps1`): owners of the chain listeners with PIDs (a TCP table through iphlpapi, no PowerShell), keeper/node, the proxy's WSL bind, counters and age of the activity marker, a detailed CONNECT roundtrip (time + first response line), the core of the guards (policy + ban, without WSL), watchdog liveness (`pnr watchdog` is visible from outside by cmdline, a watchdog inside the GUI — only from its window); a fully stopped chain — a short "normal" message instead of the full picture; log viewing (a list with sizes, tail by name `--tail N`) and rotation: the process's live log — in place (`FileLog.RotateNow`), a free file — by shifting the age chain, one held by another process — honestly "locked" | **ready** (`PrivateNetRouting.Core.Diagnostics`; CLI `pnr status`, `pnr logs list \| show <name> [--tail N] \| rotate [<name>...]`; the `DiagnosticsWindow` window — the "Diagnostics…" button: a highlighted summary, a log list with tail, rotation, opening the folder). Checked live: status and logs list/show/rotate (including port 18082 visible as busy by the running GUI, the WSL bind, and the partial legacy-scheme policy) | `proxy-status.ps1` |
| Localization | UI multilingual support: strings are in the `Strings.<code>.xaml` dictionaries (`{DynamicResource}` in XAML, `Loc.S`/`Loc.F` in code), the dictionary is swapped on the fly; the language is `ui.language` in the config: `auto` (system language) or a code, an unsupported one — English; a new language = a dictionary + a code in `AppLanguage.Supported` | **ready** (en/ru; `PrivateNetRouting.App/Localization`, a selector in the window header) | — |

Architecture notes that must not be lost during porting:

- WPF: long waits go through async/await and dispatcher queues, without blocking the UI thread;
  the UI thread is STA; a single instance through a named mutex with activation of the existing
  window. The unavoidable synchronous waits on asynchronous work (runtime start/stop in
  OnStartup/OnExit) must go through `Task.Run` (without the Dispatcher context), and Core
  start/stop paths do not capture the context (`ConfigureAwait(false)`): a blocking wait on the UI
  thread + the await continuation posted to the dispatcher queue = a deadlock and an eternally
  alive process (the regression test is `StopCompletesUnderBlockedSynchronizationContext`).
- Tray and GUI lifetime: `ShutdownMode=OnExplicitShutdown`, closing/minimizing the main window only
  hides it (the `TrayIcon` tray on WinForms NotifyIcon — WinForms is referenced for it alone, and
  global usings are removed to avoid name conflicts with WPF); the watchdog in the tray keeps
  working. Desktop shortcuts: Claude → `--launch`, "Claude (browser)" → `--launch-browser`.
- Elevation: a separate helper launch (runas) with a window-0, not a restart of the whole
  application.
- Link lifetimes: the daemon/keeper/node outlive the application exit (detached, file handles
  through cmd); the forward proxy by default lives as long as its process (the application window or
  `pnr proxy`), and with `forwardProxy.detached=true` — in a separate worker (its own exe with
  `--proxy-worker`) that outlives the command/GUI exit: bring-up/shutdown go through chain up/down
  (a GUI exit does not kill the worker), a one-off bring-up — `pnr proxy --detached`.
- Logs: two writers into one file are not allowed (interleaving corrupts the contents) — if the log
  is already held by another process (the GUI in the tray + `pnr` from a terminal), the second one
  writes to `<name>.<pid>.log`; rotation is by size at start, manually — by the file owner in place.
- Updater: the version check phase runs before the chain bring-up (no extra UAC is needed);
  installation is not interrupted; during long phases keep the activity marker fresh so that
  auto-stop does not shut the chain down under the download.
- Guard: the ban rule identifiers (hosts block markers, the NRPT rule DisplayName, the firewall
  rule Group) are in the config, and the defaults match the legacy scheme: the application and the
  old scripts maintain one and the same ban, not two. The guard status is read-only; after applying
  or removing a ban — live probes, not "it should work".
- The external protocols the semantics rest on: CONNECT roundtrip as a health check, the WSL host's
  `server.shutdown` RPC, the AppX journal as the source of the package URL, AppX Publisher
  verification.

## Repository structure

```
src/
  PrivateNetRouting.App/     # WPF: windows, single instance (mutex + event, activation of the
                             # window hidden to the tray), tray (TrayIcon: open/exit, balloon),
                             # proxy and chain status, language selection, starting Claude with the
                             # bring-up splash screen (SplashWindow), the --launch shortcut mode
                             # (Claude running → window only), the "Browser" button and the
                             # --launch-browser shortcut mode (isolated profile, "already running"
                             # note), the startup Claude update check, the update window
                             # (UpdateWindow), the "Guards" card (status and control of
                             # ban/policy/settings.json/WSL), the diagnostics window
                             # (DiagnosticsWindow: read-only state summary with highlighting,
                             # a log list with tail, rotation, opening the folder);
                             # the shared application icon
                             # (AppIcons + Assets/app.ico, the tray is a WinForms NotifyIcon);
                             # elevated helper arguments are handled before the UI
    Localization/            # string dictionaries Strings.<code>.xaml + Loc: on-the-fly dictionary
                             # swap (DynamicResource in XAML, Loc.S/Loc.F in code,
                             # Loc.LanguageChanged)
  PrivateNetRouting.Core/    # logic without UI
    Configuration/           # AppConfig (including ui.language, ui.checkUpdatesOnStart),
                             # AppLanguage (auto → system → en),
                             # loading from %APPDATA% (the template is config/*.example.json)
    Logging/                 # FileLog: DEBUG..ERROR levels (logging.level, default info) +
                             # rotation by size once at start and manual RotateNow;
                             # a file held by another process → fallback to <name>.<pid>.log
    Net/Dns/                 # DnsWire (RFC 1035/8484), DohClient, DohEndpointPool, cache resolver,
                             # TailscaleNetmapDohSource (auto-filling the pool from the tailnet config)
    Net/Socks/               # Socks5Client (no-auth, dial by IPv4)
    Net/WslGateway.cs        # WSL NAT gateway address (vEthernet (WSL...)) for the one-shot proxy bind
    Net/Proxy/               # ForwardProxyService (CONNECT proxy), parser, ProxyHealthCheck
                             # (CheckAsync-bool + ProbeAsync with timing and a status line),
                             # ForwardProxySupervisor (listener placement: in-process or a detached
                             # worker, the port is a single resource), ProxyWorker (the body of
                             # --proxy-worker for pnr and App)
    Chain/                   # ChainSupervisor (orchestration), SidecarDaemon (UAC start, stale kill),
                             # SidecarKeeper (watch-ipn), SidecarCli, ElevatedChain (runas helper)
    Launcher/                # ClaudeLauncher (bypass list, chain bring-up on failure, launch with
                             # proxy flags, warnings about the policy/running Claude);
                             # BrowserLauncher (browser with an isolated profile: Firefox user.js /
                             # Chromium-like via flags, "already running" by the profile in cmdline)
    Updater/                 # ClaudeUpdater (MSIX update orchestration: check → chain →
                             # download → Publisher verification → install), PowerShellRunner,
                             # PowerShellAppxPackageSource (Get-AppxPackage/Add-AppxPackage),
                             # PowerShellJournalUrlFinder + PackageUrl (address from the AppX journal),
                             # FragmentDownloader (chunked download with retries and resume),
                             # MsixPackage (Identity/Publisher from AppxManifest.xml)
    Guard/                   # direct-access control: EgressPolicyStore (HKLM Policies\Claude),
                             # HostsFileBlock (idempotent hosts block), PowerShellBanSystemOps
                             # (NRPT + firewall via cmdlets), ClaudeSettingsGuard (env in settings.json),
                             # WslConfigGuard (startup script + settings.json of the distros; the layer
                             # is optional: mode on/off/auto, sync per distro),
                             # WinInetSystemProxyStore + system proxy bypass, GuardService
                             # (status/operations/live probes), GuardElevatedCommands (UAC helper)
    Watchdog/                # idle auto-stop: ClaudeUsageProbe (processes/connections/marker),
                             # WslClaudeHostStopper (stopping the WSL Claude host via wsl.exe:
                             # RPC + signal escalation), WatchdogService (the loop, WSL → chain)
    SystemOps/               # ports, WMI process lookup, privileges, launching through cmd with file
                             # redirection, PowerShellRunner (IPowerShellExecutor: AppX/DnsClient/NetSecurity),
                             # TcpConnectionTable (port listeners with PID via GetExtendedTcpTable)
    Runtime/                 # ActivityMarker (the mtime marker for the watchdog)
    Diagnostics/             # DiagnosticsService (state summary: port owners, keeper/node,
                             # WSL bind, counters, marker, detailed CONNECT, the core of the guards,
                             # the watchdog), LogStore (log list/tail/external rotation)
    ProxyRuntime.cs          # composition of proxy + chain + launcher + watchdog + updater +
                             # diagnostics from AppConfig (+ RotateLog: the live log — in place,
                             # a free one — by shifting)
  PrivateNetRouting.Cli/     # headless `pnr` mode (proxy [--detached] | chain status|up|down |
                             # check | status | logs list|show <name> [--tail N]|rotate [<name>...] |
                             # launch [--check-only] [--no-splash] | browser [--check-only]
                             # [--prepare-only] [--private] [--url <url>] | update [--check] ... |
                             # watchdog | guard status [--live] | ban/policy/settings/wsl/sysproxy |
                             # config; the hidden `--proxy-worker` — the detached worker body)
tests/
  PrivateNetRouting.Tests/   # xUnit: wire formats, parser, config, the DoH pool, SOCKS5,
                             # an end-to-end proxy run on a fake SOCKS5 + echo server,
                             # the proxy supervisor on fakes (in-process/detached: spawn with a
                             # marker, idempotence, busy port, kill of the identified worker,
                             # a foreign owner is left alone, start/stop timeouts),
                             # chain orchestration on fakes (order/idempotence/failures),
                             # the Claude launcher on fakes (bypass list/order/modes), the browser
                             # launcher on fakes (bypass lists/user.js/command lines/modes/path
                             # fallback/"already running"), the watchdog on fakes
                             # (WSL→chain order, the stopper output parser, the activity probe),
                             # the guard on fakes (hosts block, settings.json env, NRPT/firewall
                             # scripts, the WSL layer: modes/status/sync, orchestration:
                             # layers/elevation/live probes), the updater on fakes (versions/journal
                             # URL, chunked download on a fake HTTP: resume/retries/cancellation,
                             # Publisher, closing Claude, the full cycle), diagnostics on fakes
                             # (summary: stopped chain/port owners/WSL bind/health/guards/watchdog,
                             # the core of the guards without WSL, ProbeAsync on a live TcpListener),
                             # logs (list/tail/rotation/busy file/fallback to a per-process
                             # file/RotateNow), the cmd argument form
config/
  appsettings.example.json   # user config template with placeholders
```

The user config is `%APPDATA%\PrivateNetRouting\appsettings.json` (created on first launch from the
default values); logs are in `%LOCALAPPDATA%\PrivateNetRouting\logs\`.

## Environment and commands

| Tool | Version | Note |
|---|---|---|
| .NET SDK | 10.0.401 LTS (WindowsDesktop.App — WPF) | a winget package; in already running sessions PATH may not see `dotnet` — use the full path `C:\Program Files\dotnet\dotnet.exe` |
| git | 2.55 (Git for Windows) | the identity is set locally in the repository (noreply) |
| gh | 2.102 | logged in; a private repository on github.com |

```bash
dotnet build
dotnet test
dotnet run --project src/PrivateNetRouting.App
dotnet format --verify-no-changes   # formatting before a commit
```

Commits: in small steps, the message in the infinitive ("Add forward proxy core", "Fix splash
focus"). Branching: `main` is the buildable state; features go in short branches or straight into
`main` (a personal repository, the main thing is not to break the build).

## Code conventions

- C# 14 / .NET 10, `net10.0-windows` for the WPF project, `net10.0` for the core and the CLI.
- Naming and style follow `dotnet format` (`.editorconfig` is in place).
- Interaction with the system (processes, registry, AppX, WSL, firewall) is only behind interfaces in
  Core; implementations are marked with documenting comments about the required privileges
  (UAC/admin).
- Logging: DEBUG/INFO/WARN/ERROR levels, the threshold is `logging.level` in the config (default
  `info`, an unknown value = `info`); all records are in English. Info carries one-off events
  (start/stop/auto-stop, important things — Warn/Error); periodic and per-request records (watchdog
  and DoH pool timers, proxy connects/resolves, chain bring-up steps, download fragments) are Debug
  only. Rotation by size at start — as in the old set; manual rotation — from diagnostics (by the
  file owner, in place).
- UI localization: strings are in the `src/PrivateNetRouting.App/Localization/Strings.<code>.xaml`
  dictionaries (XAML — `{DynamicResource Key}`, code — `Loc.S`/`Loc.F`); the language is
  `ui.language` in the config: `auto` (system language) or a code, an unsupported one — English.
  A new language = a dictionary + a code in `AppLanguage.Supported`; a test compares the key and
  placeholder sets against the en dictionary.
- The `pnr` CLI, Core strings/exceptions and log file records are English; code comments are English
  too (rule #1: the only non-English content is the UI localization dictionaries and the local
  `.secret.agents.md`).

## Working in the shell (machine legacy, briefly)

The tool shell is Git Bash; PowerShell is invoked as `pwsh -NoProfile -File <script.ps1>` (for long
scripts) or `pwsh -NoProfile -Command '…'` in bash single quotes (for short ones; never in double
quotes — bash would expand `$`). Cyrillic in the output of PS/native utilities — run
`chcp 65001 >/dev/null` first. Do not execute PS code in bash and do not grep-parse `.ps1` — only
run it. These rules are needed while behavior is being compared against the old scripts; the
application itself does not have them.