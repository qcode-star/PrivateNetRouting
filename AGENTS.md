# AGENTS.md

Private personal project: a WPF application on .NET 10 LTS (Windows Desktop) that brings under a
single window the functionality for maintaining Claude Desktop on this machine: a local proxy with
DoH resolution, a proxy chain supervisor, a launcher, idle auto-stop,
an MSIX updater, direct-access blocking, and diagnostics. Below are the rules that must not be
broken and the architecture of the application.

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

**Source documents outside the repository** (machine logs, personal notes) contain real addresses
and identifiers — they must not be copied into the repository verbatim. Carry over only the
sanitized essence: mechanics, protocols, sequence of actions — without addresses, names, or domains.

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

## Rule #4: nothing in autostart without explicit confirmation

Do not register or add anything to autostart on your own initiative (scheduled tasks, services,
autostart keys, background processes that outlive the session) — even if the feature is ready and
the benefit is obvious. First show what exactly will be created and where, wait for a "yes". The
same applies to removing existing autostart entries. The rule extends to the application too:
background modes (tray, watchdog) — only on an explicit user action.

## Rule #5: no drivers and no injection

Do not use and do not propose tools with drivers or process injection (Proxifier-like tools, WFP
interception, DLL injection). All routing is solved by the proxy and the application's policies.
This is a fundamental limitation of the scheme, not a shortcoming.

## Rule #6: routes, DNS, hosts, firewall — state before, live check after

Before changes to routes/DNS/hosts/NRPT/firewall, look at the current state first; afterwards, verify
the result on live traffic, not "it should work". Separately remember: the application has layers of
protection against direct access (managed policy, hosts, NRPT, firewall) — change them only through
the application's modules, with a transparent status of what was done.

## Rule #7: project md files — current state only

In all md files of the repository we record only the current actual state: no history of "it was
like this, then we changed it", no check dates, no session references. When a fact stops being true
it is rewritten or deleted. AGENTS.md is a living document: a module is implemented — update the
row, don't append a note.

## Architecture

A single WPF application (single instance, tray + windows) plus a headless CLI mode. Core modules
do not depend on the UI (testability); the UI is a thin wrapper.
Application modes (the owner's decision):

- **normal GUI** (`PrivateNetRouting.App` with no arguments): the status/control window; the tray
  icon lives as long as the application runs; minimizing and closing the window hide it to the tray
  (the proxy and the started idle stopper keep working — the stopper is a detached process and
  survives even the real exit); the real exit is only from the tray menu — "Exit" leaves the chain
  running in the background (the in-process forward proxy is handed over to a detached worker, the
  next start sees the chain as it was), "Exit with full chain stop" brings it down first — or
  ending the Windows session; a second launch shows the hidden window;
- **shortcut GUI** (`--launch`): this is how the Claude desktop shortcut works — if Claude is
  already running, the application window is simply shown; if not, the window draws the bring-up in
  the chain card (tunnel, checks) and then starts Claude with proxy flags;
- **console** (`pnr`, a separate exe): the headless commands.

Ports per the owner's decision: **18081** (forward proxy, the address for
the client), **18082** (daemon HTTP), **18083** (daemon SOCKS5). Every module takes its addresses/ports/names from the
config; they change only by explicitly editing the user config.

| Module | What it does | Implementation |
|---|---|---|
| ForwardProxy | local CONNECT proxy: DoH resolution of names through the resolvers from the config, dialing through the SOCKS5 upstream of the userspace sidecar, bind on localhost and the WSL gateway, activity marker (mtime) for the watchdog; the listener placement mode is decided by ForwardProxySupervisor: in-process (default) or a detached worker (`forwardProxy.detached`, its own exe with the `--proxy-worker` marker, spawned through cmd with redirection) — the port is a single resource: a busy one is not bound by anyone, stop kills only the worker identified by cmdline, a foreign owner is left alone (warning to the log) | `PrivateNetRouting.Core.Net.*`; CLI `pnr proxy [--detached]` |
| ChainSupervisor | idempotent chain bring-up/shutdown (userspace tailscaled daemon → watch-ipn keeper → up → forward proxy → health check); the daemon start/stop elevation is `chain.daemonElevation`: auto (default) — direct on tailscale 1.104.1+ (`--windows-mode=dev`, the pipe is owned by the user — that release removed the Administrators owner from the pipe SDDL), through a runas copy of itself on older builds (the only UAC), "never"/"always" force a mode (the modes are version-locked: a 1.104.1+ daemon rejects a custom socket without the dev flag even elevated); "our" processes are identified by the statedir/pipe in the command line; daemon/keeper logs are file handles through cmd, surviving application exit. Daemon liveness is determined by the responsiveness of its named pipe (an elevated daemon hides its CommandLine from a non-elevated WMI reader — Windows 11 24H2+; an elevated stop still kills processes by cmdline search) | `PrivateNetRouting.Core.Chain`; CLI `pnr chain up\|down\|status`, `pnr check` |
| Launcher | starting Claude Desktop with `--proxy-server`/`--proxy-bypass-list` (the bypass list is built from the config domains, both spellings of each zone; the base entries — localhost/private ranges — are fixed in the code), checking and bringing up the chain (live CONNECT, then an idempotent chain up), the bring-up steps drawn live in the chain card of the main window (spinner, elapsed time, the current step; on a live chain nothing is drawn at all), control warnings to the log about the `egressProxyUrl` managed policy and an already running Claude without flags, mode flags (check-only/no-splash); the GUI shortcut mode `--launch`: Claude already running (exact process name) — only the application window, not running — a full launch whose bring-up runs in the window | `PrivateNetRouting.Core.Launcher`; CLI `pnr launch [--check-only] [--no-splash]` (the flag is accepted and ignored: the progress belongs to the GUI window); the Claude desktop shortcut → `PrivateNetRouting.App --launch` |
| Watchdog | idle auto-stop: periodic activity check (claude* processes, connections to the proxy, activity marker freshness; the first poll is after the interval, every timer tick is Debug, Info only carries a reason change and the auto-stop itself), when idle — graceful stop of the WSL Claude host (RPC server.shutdown with its own binary, SIGTERM→SIGKILL escalation on orphans, the distro is not brought up just for the check), then stopping the chain with the existing orchestrator; the start is only explicit (`pnr watchdog` — the foreground CLI form / the toggle in the chain card): the GUI starts a detached worker (its own exe with the `--watchdog-worker` marker, spawned through cmd with redirection) that outlives the application and switches itself off after the idle stop; `watchdog.autoStart` (default on) — after every successful chain raise the orchestrator's hook re-arms the stopper fire-and-forget, the checkbox sits in the chain card row next to the toggle; liveness is read from process command lines, the stop kills only the identified stoppers, and the worker is never written to autostart (rule #4) | `PrivateNetRouting.Core.Watchdog` (WatchdogService + WatchdogSupervisor/Worker); CLI `pnr watchdog`; the "Idle stop" row of the chain card (the interval and the WSL distro are under `watchdog.*`) |
| Updater | manual MSIX update: version check BEFORE the chain bring-up ("nothing to install" — no UAC), the package address from Claude's own update feed — asked through the already-live proxy, without raising the chain (the AppX journal where the built-in updater leaves a failed-download address is the fallback; or manual entry), multi-threaded chunked download through the application's own forward proxy, Publisher verification, closing Claude, `Add-AppxPackage`; a GUI with steps/progress/stop button, a pause before installation and a deferred installation (`--install-from`); during the download the activity marker is kept fresh (auto-stop does not shut the chain down under the download); on GUI start — a background update check (read-only on versions, the feed and the journal, no chain and no UAC; `ui.checkUpdatesOnStart`): if there is one — a line next to the launch buttons and a tray balloon, and the update window's own check or a finished installation keeps that line in sync (a stale one is dropped); after a successful installation the window closes itself in 30 s with a visible countdown (any interaction keeps it open); a full run with no address yet raises the chain and asks the feed once more | `PrivateNetRouting.Core.Updater` + `UpdateWindow` in App; CLI `pnr update [--check] [--download-only] [--force] [--keep-file] [--keep-running] [--url <url>] [--install-from <path>]` |
| Guard | direct-access control: the managed policy `egressProxyUrl` (+`disableWslSessions=0`); a local ban in three layers — a hosts block, one NRPT rule for all subtrees, a firewall outbound Block by subnet (the rule identifiers are configurable; the application maintains a ban with these identifiers instead of creating a parallel one); env in Claude Code settings.json (Windows and WSL: the application proxy + NO_PROXY from the config domains; in WSL also a startup script with the NAT gateway address; the layer is entirely optional: `guard.wsl.mode` = on/off/auto and a per-distro choice of syncing); the WinINET system proxy on/off. The status of all guards is read-only (a GUI card, `pnr guard status [--live]`); the ban and the policy go through an elevated helper, and after a ban — live probes (DNS/direct/proxied) | `PrivateNetRouting.Core.Guard` + the "Guards" card in App; CLI `pnr guard status [--live] \| ban on\|off \| policy on\|off \| settings sync \| wsl sync \| sysproxy on\|off` |
| BrowserLauncher | launching a browser with an isolated profile and the application proxy, without touching system settings: Firefox — a separate profile with user.js, rewritten on every launch (the proxy address from `forwardProxy.listen`, `no_proxies_on` — the `launcher.bypassDomains` domains in both forms + `*.tld` from `browser.directTlds`, `failover_direct=false`, `trr.mode=5`), started with `--new-instance` (otherwise the URL would go to the everyday instance without a proxy); Chromium-like — `--proxy-server`/`--proxy-bypass-list`/`--user-data-dir`/`--no-first-run` (a separate directory is mandatory: a running instance ignores the flags); the chain is like the Claude launcher's (live CONNECT, on failure an idempotent bring-up drawn in the chain card while waiting); the browser is chosen by `browser.type` (firefox/chromium, an unknown value — firefox), the private window — `browser.private` or a flag; an already running instance of ours (cmdline with our profile) is not duplicated — a note in the application window | `PrivateNetRouting.Core.Launcher.BrowserLauncher`; CLI `pnr browser [--check-only] [--prepare-only] [--private] [--url <url>]`; the "Browser" button in the main window; GUI shortcut mode `--launch-browser`; the "Claude (browser)" shortcuts point at the application |
| Diagnostics | a read-only state summary: owners of the chain listeners with PIDs (a TCP table through iphlpapi, no PowerShell), keeper/node, the proxy's WSL bind, counters and age of the activity marker, a detailed CONNECT roundtrip (time + first response line), the core of the guards (policy + ban, without WSL), idle stopper liveness (visible from outside by the command line: the detached worker `--watchdog-worker` or the foreground `pnr watchdog`); a fully stopped chain — a short "normal" message instead of the full picture; log viewing (a list with sizes, tail by name `--tail N`) and rotation: the process's live log — in place (`FileLog.RotateNow`), a free file — by shifting the age chain, one held by another process — honestly "locked" | `PrivateNetRouting.Core.Diagnostics` + `DiagnosticsWindow` in App (the "Diagnostics…" button: a highlighted summary, a log list with tail, rotation, opening the folder); CLI `pnr status`, `pnr logs list \| show <name> [--tail N] \| rotate [<name>...]` |
| Localization | UI multilingual support: strings are in the `Strings.<code>.xaml` dictionaries (`{DynamicResource}` in XAML, `Loc.S`/`Loc.F` in code), the dictionary is swapped on the fly; the language is `ui.language` in the config: `auto` (system language) or a code, an unsupported one — English; a new language = a dictionary + a code in `AppLanguage.Supported` | `PrivateNetRouting.App/Localization`; en/ru; a selector in the window header |

Architecture notes:

- WPF: long waits go through async/await and dispatcher queues, without blocking the UI thread;
  the UI thread is STA; a single instance through a named mutex with activation of the existing
  window. The unavoidable synchronous waits on asynchronous work (runtime start/stop in
  OnStartup/OnExit) must go through `Task.Run` (without the Dispatcher context), and Core
  start/stop paths do not capture the context (`ConfigureAwait(false)`): a blocking wait on the UI
  thread + the await continuation posted to the dispatcher queue = a deadlock and an eternally
  alive process (the regression test is `StopCompletesUnderBlockedSynchronizationContext`).
- Tray and GUI lifetime: `ShutdownMode=OnExplicitShutdown`, closing/minimizing the main window only
  hides it (the `TrayIcon` tray on WinForms NotifyIcon — WinForms is referenced for it alone, and
  global usings are removed to avoid name conflicts with WPF); the tray menu is show window,
  chain up / chain down / launch Claude (the same code paths as the chain card and the launch
  button, enablement follows the card's buttons), "Exit" (the chain keeps working: an in-process
  proxy is handed to a detached worker) and "Exit with full chain stop"; the started idle stopper
  keeps working — it lives in its own detached process and survives even the real exit. Desktop
  shortcuts: Claude → `--launch`, "Claude (browser)" → `--launch-browser`.
- Elevation: a separate helper launch (runas) with a window-0, not a restart of the whole
  application.
- Link lifetimes: the daemon/keeper/node outlive the application exit (detached, file handles
  through cmd); the forward proxy by default lives as long as its process (the application window or
  `pnr proxy`), and with `forwardProxy.detached=true` — in a separate worker (its own exe with
  `--proxy-worker`) that outlives the command/GUI exit: bring-up/shutdown go through chain up/down
  (a GUI exit does not kill the worker), a one-off bring-up — `pnr proxy --detached`; the tray
  "Exit" with the chain kept hands an in-process listener over to such a worker
  (`ForwardProxySupervisor.DetachAsync`); the idle
  stopper started from the GUI — a separate worker (`--watchdog-worker`) that outlives the exit on
  its own and disappears after the idle stop (the CLI form `pnr watchdog` — foreground, in the
  terminal).
- Logs: two writers into one file are not allowed (interleaving corrupts the contents) — if the log
  is already held by another process (the GUI in the tray + `pnr` from a terminal), the second one
  writes to `<name>.<pid>.log`; rotation is by size at start, manually — by the file owner in place.
- Updater: the version check phase runs before the chain bring-up (no extra UAC is needed);
  installation is not interrupted; during long phases keep the activity marker fresh so that
  auto-stop does not shut the chain down under the download.
- Guard: the ban rule identifiers (hosts block markers, the NRPT rule DisplayName, the firewall
  rule Group) are in the config; the application maintains a ban with these identifiers, not a
  parallel one. The guard status is read-only; after applying or removing a ban — live probes,
  not "it should work".
- The external protocols the semantics rest on: CONNECT roundtrip as a health check, the WSL host's
  `server.shutdown` RPC, Claude's update feed (`updater.updateFeedUrl`, the same endpoint the
  built-in updater polls) as the primary source of the package URL with the AppX journal as the
  fallback, AppX Publisher verification.
- UI tokens: everything visual comes from `App.xaml` — semantic brush names (window/band/card/code,
  three weights of text, one accent, the state colours), a 4px spacing grid, two corner radii and a
  four-step type scale, with control styles on top. Windows and code reference the keys (code goes
  through `Theme`) and never hardcode a colour, size or radius; a state is always a word next to the
  colour, never colour alone; machine data (addresses, paths, counters, log lines) is monospaced
  with tabular figures.

## Repository structure

```
src/
  PrivateNetRouting.App/     # WPF: windows, single instance (mutex + event, activation of the
                             # window hidden to the tray), tray (TrayIcon: show window, chain
                             # up/down, launch Claude, two exits, balloon),
                             # language selection, the chain card — the single lifecycle control
                             # (idempotent bring-up/stop of the whole chain, the animated rail
                             # that runs the bring-up live while launching Claude or a browser,
                             # status rows: the PNR listeners with connection counters, the
                             # tailscale listeners, the config with its actions (open the
                             # folder / reload), the idle stopper with its status, the
                             # auto-start flag and the toggle; collapsible to one compact row —
                             # overall status dot + word and the same two buttons, a chain
                             # operation expands the card and collapses it back on success,
                             # the choice is remembered in ui.chainCardExpanded),
                             # the --launch shortcut mode
                             # (Claude running → window only), the "Browser" button and the
                             # --launch-browser shortcut mode (isolated profile, "already running"
                             # note), the startup Claude update check, the update window
                             # (UpdateWindow), the "Guards" card (status and control of
                             # ban/policy/settings.json/WSL; collapsible to one row — aggregate
                             # status dot + word and a single combined action: "enable all"
                             # = ban + policy + both syncs, "disable all" = ban + policy;
                             # ban and policy go as one elevation (--elevated-guard-all-on/off),
                             # remembered in ui.guardsCardExpanded), the diagnostics window
                             # (DiagnosticsWindow: read-only state summary with highlighting,
                             # a log list with tail, rotation, opening the folder);
                             # the shared application icon
                             # (AppIcons + Assets/app.ico, the tray is a WinForms NotifyIcon);
                             # elevated helper arguments are handled before the UI
    Localization/            # string dictionaries Strings.<code>.xaml + Loc: on-the-fly dictionary
                             # swap (DynamicResource in XAML, Loc.S/Loc.F in code,
                             # Loc.LanguageChanged)
    ChainRail.cs             # the chain rail of the main window: five stages as markers on a link,
                             # built in code so a stage can light up (colour fade, a pop when it
                             # completes, a breath while it is being brought up)
    StepRail.cs              # one row of a vertical rail (the update window's phases), sharing the
                             # marker vocabulary of the chain rail: hollow / check / cross
    Theme.cs                 # App.xaml tokens for the code paths: state → brush, state → word
    WindowTheme.cs           # dark DWM title bar; unsupported builds keep the system one
  PrivateNetRouting.Core/    # logic without UI
    Configuration/           # AppConfig (including ui.language, ui.checkUpdatesOnStart,
                             # ui.chainCardExpanded, ui.guardsCardExpanded),
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
    Chain/                   # ChainSupervisor (orchestration), SidecarDaemon (direct or elevated
                             # start by chain.daemonElevation, stale kill), TailscaleVersionProbe
                             # (the auto elevation decision), SidecarKeeper (watch-ipn),
                             # SidecarCli, ElevatedChain (runas helper)
    Launcher/                # ClaudeLauncher (bypass list, chain bring-up on failure, launch with
                             # proxy flags, warnings about the policy/running Claude);
                             # BrowserLauncher (browser with an isolated profile: Firefox user.js /
                             # Chromium-like via flags, "already running" by the profile in cmdline)
    Updater/                 # ClaudeUpdater (MSIX update orchestration: check → chain →
                             # download → Publisher verification → install), PowerShellRunner,
                             # PowerShellAppxPackageSource (Get-AppxPackage/Add-AppxPackage),
                             # UpdateFeedUrlFinder (address from Claude's update feed through the
                             # live proxy) + PowerShellJournalUrlFinder + PackageUrl (address from
                             # the AppX journal),
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
                             # RPC + signal escalation), WatchdogService (the loop, WSL → chain),
                             # WatchdogSupervisor (the detached worker lifecycle: start by the
                             # --watchdog-worker marker through cmd, liveness/stop by command
                             # lines), WatchdogWorker (the worker body)
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
                             # the idle stopper supervisor on fakes (liveness by command lines,
                             # idempotent start, kill of the identified stoppers only),
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

The test project references `PrivateNetRouting.Core` only, plus `Fakes.cs` (the shared fake
implementations — add new fakes there). The WPF app and the CLI therefore have no automated
coverage: for changes in them, build and exercise the real window/command, and keep logic in Core
so it stays testable.

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

From the repository root, `dotnet build` / `dotnet test` act on all four projects
(`PrivateNetRouting.slnx`, the XML solution format). A focused run of one module:
`dotnet test --filter "FullyQualifiedName~Guard"` — test classes are grouped per module
(`GuardTests`, `UpdaterTests`, `WatchdogTests`, …). `dotnet format` is the source of truth for
style; run it before committing.

Commits: in small steps, the message in the infinitive ("Add forward proxy core", "Fix chain card
focus"). Branching: `main` is the buildable state; features go in short branches or straight into
`main` (a personal repository, the main thing is not to break the build).

## Code conventions

- C# 14 / .NET 10; every project targets `net10.0-windows` (Core and the CLI need Windows APIs —
  WMI, iphlpapi), so the whole solution is Windows-only.
- `Directory.Build.props` sets `Nullable=enable`, `ImplicitUsings=enable` and
  `TreatWarningsAsErrors=true`: any compiler warning fails `dotnet build`. Fix the warning; do not
  silence it with `#pragma warning disable` or `NoWarn`. The WPF app is `WinExe`, the CLI's
  `AssemblyName` is `pnr`, and Core exposes internals to the test project (`InternalsVisibleTo`).
  Keep `Version`/`Company`/`Authors` as the anonymous placeholders they are (rule #2) — never fill
  in real author metadata.
- Core's only external package is `System.Management` (WMI process lookup). Prefer the BCL and
  `SystemOps` wrappers; adding a dependency needs a reason.
- Naming and style follow `dotnet format` (`.editorconfig` is in place).
- Interaction with the system (processes, registry, AppX, WSL, firewall) is only behind interfaces in
  Core; implementations are marked with documenting comments about the required privileges
  (UAC/admin).
- Logging: DEBUG/INFO/WARN/ERROR levels, the threshold is `logging.level` in the config (default
  `info`, an unknown value = `info`); all records are in English. Info carries one-off events
  (start/stop/auto-stop, important things — Warn/Error); periodic and per-request records (watchdog
  and DoH pool timers, proxy connects/resolves, chain bring-up steps, download fragments) are Debug
  only. Rotation by size at start; manual rotation — from diagnostics (by the
  file owner, in place).
- UI localization: strings are in the `src/PrivateNetRouting.App/Localization/Strings.<code>.xaml`
  dictionaries (XAML — `{DynamicResource Key}`, code — `Loc.S`/`Loc.F`); the language is
  `ui.language` in the config: `auto` (system language) or a code, an unsupported one — English.
  A new language = a dictionary + a code in `AppLanguage.Supported`; a test compares the key and
  placeholder sets against the en dictionary.
- The `pnr` CLI, Core strings/exceptions and log file records are English; code comments are English
  too (rule #1: the only non-English content is the UI localization dictionaries and the local
  `.secret.agents.md`).

## Where to look first

- `.secret.agents.md` (repository root, git-ignored, the deliberate exception to rule #1) — the
  machine-specific facts: real addresses, distro and user names, references to sources. Read it
  when a task needs real environment values; never copy its contents into a commit or another file.
- `config/appsettings.example.json` — the authoritative config schema with placeholders. A new
  setting goes here and into `AppConfig` before any code reads it (rule #3).
- `README.md` — the human-facing module overview.
- `.zcodeignore` — the block above the sync marker is rewritten from `.gitignore`; put agent-only
  rules below the marker line, or they will be lost on the next sync.
- `.zcode/` is not git-ignored and holds no tracked files; anything written there (e.g. under
  `plans/`) shows up as untracked in `git status`. Keep scratch files out of commits.

## Working in the shell (environment quirks, briefly)

The tool shell is Git Bash; PowerShell is invoked as `pwsh -NoProfile -File <script.ps1>` (for long
scripts) or `pwsh -NoProfile -Command '…'` in bash single quotes (for short ones; never in double
quotes — bash would expand `$`). Cyrillic in the output of PS/native utilities — run
`chcp 65001 >/dev/null` first. Do not execute PS code in bash and do not grep-parse `.ps1` — only
run it. These quirks belong to the shell environment, not to the application.