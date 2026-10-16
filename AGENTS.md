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

The repository is public and must stay **as anonymous as possible**: it does not contain and must
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

Before every commit, review the diff: IP addresses (only `127.0.0.1`, `0.0.0.0`, the documentation
ranges of RFC 5737 (`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`) and RFC 3849
(`2001:db8::/32`) as samples — the upstreams are IP-only, so the template needs one —,
`*.example` host samples, and documented public constants such as API hosts in templates are
allowed), unfamiliar
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
  from the default values of the code). The repository example is `config/appsettings.example.json` with placeholders.
- Nothing environment-specific is hardcoded in the code: addresses, ports, paths to binaries
  (tailscale, wsl, browsers), the WSL distro and user name, the health-check target, bypass domain
  lists, timeouts, idle thresholds.
- The user config may hold the SOCKS5 upstream's credentials (`chain.socks5.user`/
  `chain.socks5.password`) — it is the user's own file and its owner decides what it holds; the
  repository must never receive real proxy addresses or credentials, with or without a password
  (the example holds placeholders only). Any other secrets — only Windows Credential Manager /
  DPAPI, never a plaintext file.

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
| ForwardProxy | local CONNECT proxy. Why it exists: the tailscale sidecar's own proxies (SOCKS5 and HTTP) resolve names with the system resolver — the tsdial dialer never consults the netstack DNS (a long-open upstream limitation of userspace mode) — and the direct-access ban blackholes exactly the AI names there; besides that, the clients (the managed policy of Claude, `HTTP(S)_PROXY` of Claude Code and Codex) speak HTTP CONNECT, which a plain SOCKS5 upstream does not answer — this proxy is the translator. It refuses (403) a CONNECT to a loopback or link-local target — through the chain that would be the upstream's own host — and warns in the log when `forwardProxy.listen` is not a loopback address (the proxy has no client authentication). An upstream with a mixed HTTP+SOCKS inbound (e.g. sing-box) makes the local proxy redundant in the socks5 mode: `forwardProxy.enabled=false` runs no local proxy at all and every client is pointed at `chain.socks5.upstream` itself — the one address must then answer HTTP CONNECT as well as SOCKS5, the mixed inbound does the CONNECT-to-SOCKS translation (the "applications proxy" endpoint, AppProxyEndpoint: launchers, the policy and settings env, the WSL sync, the updater downloads, the health checks, the browser profile with the forward-proxy egress all take their address from it; the tailscale mode refuses the bring-up with an error when the flag is off — the proxy is load-bearing there). The http mode: the provided plain HTTP proxy (`chain.http.upstream`) speaks CONNECT itself, so the local proxy never runs there at all (`forwardProxy.enabled` is ignored) and that address is the AppProxyEndpoint for everyone. The tailscale mode: DoH resolution of names (the pool is auto-filled from the tailnet netmap — any DoH resolvers of the tailnet DNS config: http and https, by IP or by name, names resolved once at discovery and kept for TLS SNI; the optional `forwardProxy.dohSeed` adds extra endpoints) and dialing by IP through the SOCKS5 upstream — the sidecar's own listener (`chain.tailscale.socksListen`): the address is derived from the daemon's listener and cannot drift from it; `chain.socks5.upstream` (with its RFC 1929 user/pass) belongs to the socks5 mode alone. The socks5 mode: the trusted external upstream (`chain.socks5.upstream`) resolves on its side — the target name rides the CONNECT itself (ATYP=3), no DoH machinery exists there at all (also with optional RFC 1929 user/pass), bind on localhost and the WSL gateway, activity marker (mtime) for the watchdog; in a skipped shape (the http mode, or socks5 with the flag off) the whole proxy does not exist — the supervisor is an honest no-op (never running, nothing to start/stop/detach), `pnr proxy` says so and exits; the listener placement mode is decided by ForwardProxySupervisor: in-process (default) or a detached worker (`forwardProxy.detached`, its own exe with the `--proxy-worker` marker, spawned through cmd with redirection) — the port is a single resource: a busy one is not bound by anyone, stop kills only the worker identified by cmdline; an in-process owner of ours (the GUI in the tray, a foreground pnr proxy) is asked through a session-local named event (NamedEventProxyStopChannel) to stop its own listener — the detached idle stopper cannot kill the GUI (the owner serializes such a request with its own start/detach/stop, and a request meant for a listener that is already gone is ignored); any other foreign owner — an owner whose event cannot be opened (an elevated one) included — is left alone (warning to the log) | `PrivateNetRouting.Core.Net.*`; CLI `pnr proxy [--detached]` |
| ChainSupervisor | idempotent chain bring-up/shutdown; `chain.mode` picks the shape (a change applies after a restart). "tailscale" (default): userspace tailscaled daemon → watch-ipn keeper → node (a node without a key — an interactive `tailscale login` first: the auth URL rides the progress, the GUI opens the page in the browser, the CLI prints it; the control server: chain.tailscale.loginServer, or — empty — the system-wide tailscale's one picked up from its prefs; bounded by loginWaitSeconds) → forward proxy → health check; the daemon start/stop elevation is `chain.tailscale.elevation`: auto (default) — direct on tailscale 1.104.1+ (`--windows-mode=dev`, the pipe is owned by the user — that release removed the Administrators owner from the pipe SDDL), through a runas copy of itself on older builds (the only UAC), "never"/"always" force a mode (the modes are version-locked: a 1.104.1+ daemon rejects a custom socket without the dev flag even elevated). "socks5": one egress check of the provided external upstream (a CONNECT-by-name of the health host through the upstream — the upstream resolves it on its side, polled to the same deadline) → forward proxy → health check — no UAC, no tailscale processes, no DoH machinery (the names ride the CONNECT; the DoH pool is not even built), stop takes down only the proxy. "http": the same shape against the provided plain HTTP proxy (`chain.http.upstream`) — the egress check is the HTTP CONNECT roundtrip straight against it (the upstream speaks CONNECT natively and resolves the name on its side), no SOCKS5 exists anywhere in the shape, no local proxy ever runs (`forwardProxy.enabled` is ignored), stop has nothing local to stop; in the socks5 mode with the local proxy skipped (`forwardProxy.enabled=false`) the proxy link does not exist the same way: egress check → health against the upstream itself (the skipped shapes shrink the chain rail to two stages, ChainState.ProxyRunning is null — "not applicable", not false). The bring-up honours its cancellation token: a cancellation surfaces as OperationCanceledException, never wrapped into a link's ChainException (the GUI stop during a bring-up relies on it). "Our" processes are identified by the statedir/pipe in the command line; daemon/keeper logs are file handles through cmd, surviving application exit. Daemon liveness is determined by the responsiveness of its named pipe (an elevated daemon hides its CommandLine from a non-elevated WMI reader — Windows 11 24H2+; an elevated stop still kills processes by cmdline search) | `PrivateNetRouting.Core.Chain`; CLI `pnr chain up\|down\|status`, `pnr check` |
| Launcher | starting Claude Desktop with `--proxy-server`/`--proxy-bypass-list` (the bypass list is built from the config domains, both spellings of each zone; the base entries — localhost/private ranges — are fixed in the code), checking and bringing up the chain (live CONNECT, then an idempotent chain up), the bring-up steps drawn live in the chain card of the main window (spinner, elapsed time, the current step; on a live chain nothing is drawn at all), control warnings to the log about the `egressProxyUrl` managed policy and an already running Claude without flags (the log warning is paired with an offer to the owner: an application started outside the launcher is single-instance, so the flags of a new start never reach the processes of the running copy — the GUI shows a dialog, the CLI asks on the console, `--restart` answers without asking, and on consent the copy is closed gracefully (WM_CLOSE, then a forced tree kill of what survived the `restartGraceSeconds` wait) and started again through the proxy; `RunningApp` holds the scan, the report and the close for both applications), mode flags (check-only/no-splash); the GUI shortcut mode `--launch`: Claude already running (exact process name) — only the application window, not running — a full launch whose bring-up runs in the window. The Codex desktop app (OpenAI, a Microsoft Store MSIX; `codex.*` config) is started by the same contract: live CONNECT → idempotent chain up → a refresh of the proxy env in `CODEX_HOME/.env` (the desktop app strips the inherited proxy env from the processes it spawns, so the embedded CLI core takes the proxy from that file — see `CodexEnvFile`; after a WSL codex session it may hold the NAT gateway address, which is unreachable from Windows once WSL is down) → the same Chromium flags **plus the proxy env variables** (HTTP(S)_PROXY/ALL_PROXY/NO_PROXY — a belt-and-braces path for any Codex child that does inherit them); the launch goes through `Invoke-CommandInDesktopPackage` targeting the manifest entry point `app\ChatGPT.exe` (the packaged stub loses arguments, a DCOM activation loses the env); an already running Codex without flags — the same warning to the log | `PrivateNetRouting.Core.Launcher` (ClaudeLauncher + CodexLauncher); CLI `pnr launch [--check-only] [--no-splash] [--restart]` (the flag is accepted and ignored: the progress belongs to the GUI window), `pnr codex launch [--check-only] [--restart]`; the Claude desktop shortcut → `PrivateNetRouting.App --launch` |
| Watchdog | idle auto-stop: periodic activity check (claude* and codex* processes (`codex.processName`), connections to the proxy, connections to the chain's egress listener (`chain.tailscale.socksListen` — a browser pointed there by `browser.egress=chain` goes straight there, past the application proxy, so browsing keeps the chain from being stopped under an open browser; the default forward-proxy egress is covered by the proxy-connection check; the external-upstream modes (socks5, http) have no local egress to guard), activity marker freshness (the signal is skipped when the local proxy does not run — a skipped shape, the http mode or socks5 with the flag off: nobody touches the marker, connections to the upstream's port remain the source); the first poll is after the interval, every timer tick is Debug, Info only carries a reason change and the auto-stop itself), when idle — stopping the app hosts inside the RUNNING configured WSL distros (Claude: RPC server.shutdown with its own binary and SIGTERM→SIGKILL escalation on orphans; Codex: leftover processes recognized by CODEX_HOME in the environment, SIGTERM→SIGKILL; no distro is brought up just for the check), then stopping the chain with the existing orchestrator (a failed chain stop — a declined UAC — is retried on the next ticks, three failures in a row make the stopper give up with an error and exit; activity regained in between resets the count); the stopper lives only over a live chain — a completed chain stop takes it down through the orchestrator's chain-stopped hook (the calling stopper spares its own process and exits right after), it cannot be armed over a down chain (the GUI Start is disabled, `pnr watchdog` refuses), and the explicit start is the toggle in the chain card or the foreground CLI form: the GUI starts a detached worker (its own exe with the `--watchdog-worker` marker, spawned through cmd with redirection) that outlives the application and switches itself off after the idle stop; `watchdog.autoStart` (default on) — after every successful chain raise the orchestrator's hook re-arms the stopper fire-and-forget, the checkbox sits in the chain card row next to the toggle; liveness is read from process command lines, the stop kills only the identified stoppers, and the worker is never written to autostart (rule #4) | `PrivateNetRouting.Core.Watchdog` (WatchdogService + WatchdogSupervisor/Worker); CLI `pnr watchdog`; the "Idle stop" row of the chain card (the interval is under `watchdog.*`, the distro list and paths are under `wsl.*`) |
| Updater | manual MSIX update: version check BEFORE the chain bring-up ("nothing to install" — no UAC), the package address from Claude's own update feed — asked through the already-live proxy, without raising the chain (the AppX journal where the built-in updater leaves a failed-download address is the fallback; or manual entry), multi-threaded chunked download through the application's own forward proxy, Publisher verification, closing Claude, `Add-AppxPackage`; a GUI with steps/progress/stop button, an offer to raise the chain once when no source has an address (the check then repeats over the live chain; a refusal leaves the manual entry), a pause before installation and a deferred installation (`--install-from`); during the download the activity marker is kept fresh (auto-stop does not shut the chain down under the download); on GUI start — a background update check (read-only on versions, the feed and the journal, no chain and no UAC; `ui.checkUpdatesOnStart`): an update is announced only with a concrete offered version ("no address known" is not "an update exists") — an accent dot on the update button (the versions in its tooltip) and a tray balloon, and the update window's own check or a finished installation keeps the dot in sync (a stale one is dropped); after a successful installation the window closes itself in 30 s with a visible countdown (any interaction keeps it open); a full run with no address yet raises the chain and asks the feed once more. The Codex desktop app updates through the Microsoft Store (CodexUpdater + the WinRT AppInstallManager; the Store CDN is not part of the ban — no chain, no UAC, no downloads of our own): the check asks the Store catalog by the product id (`codex.update.storeProductId`) and OpenAI's own version manifest for the offered version — through the live proxy only, the host is under the ban (no manifest answer never hides an update); the update hands the package family name to the Store and polls the install item (`codex.update.updateTimeoutSeconds` limit, "Stop" cancels the Store item); a running Codex is closed first; a finished check or installation keeps the Codex update dot in sync | `PrivateNetRouting.Core.Updater` + `UpdateWindow` and `CodexUpdateWindow` in App; CLI `pnr update [--check] [--download-only] [--force] [--keep-file] [--keep-running] [--url <url>] [--install-from <path>]`, `pnr codex update [--check]` |
| Guard | direct-access control: the managed policy `egressProxyUrl` (+`disableWslSessions=0`); a local ban in three layers — a hosts block, one NRPT rule for all subtrees, a firewall outbound Block by subnet (the rule identifiers are configurable; the application maintains a ban with these identifiers instead of creating a parallel one); the settings of both applications in one sync — env in Claude Code settings.json (the application proxy + NO_PROXY from the config domains) and the Codex layer — the flag `[features] respect_system_proxy = true` in config.toml (a belt-and-braces for the sysproxy layer only: upstream the flag is not reliably honoured — openai/codex#39237, #29958 — connectivity never rests on it) plus the proxy env (HTTP(S)_PROXY/ALL_PROXY/NO_PROXY) in the `.env` of the same CODEX_HOME — the embedded CLI core loads that file after its own start, applying the values over the inherited environment, so it is what actually carries the proxy to the core (both files get surgical edits: every other line survives byte for byte, a backup to `.pnr-bak-*` before a rewrite — a state already present among the backups is not backed up again, the matching copy is touched instead, so mtime order mirrors the preserved states; the copies live in three numbered slots, the oldest one reused, fresh files are created when Codex has none yet); in WSL also a startup script with the NAT gateway address and the Codex proxy env layer — a profile.d snippet that exports the application proxy only to the shells Codex itself starts inside the distro (the desktop app passes `CODEX_HOME` into the distro for its own processes and runs them through a login shell; the codex command name is the fallback) and rewrites that CODEX_HOME `.env` to the current gateway address (the core's .env load overrides the exports, so the loopback form the Windows side keeps would win otherwise; the launcher refreshes it back before every launch), recomputes the WSL NAT gateway address at every shell start (127.0.0.1 inside a distro is the distro itself) and leaves ordinary shells, apt, pip and git on the direct route; the sync also wires the distro's boot layer — the `[boot]` command of `/etc/wsl.conf` is pointed at a marked runner (`/etc/wsl.d/startup.sh`) that executes the startup directory in name order, a foreign command in the slot is preserved as `00-user-boot-command.sh` (the 00- name keeps it first), a foreign file already at the runner path is replaced, and a command that merely references the runner path (`bash …`, `… &`, a redirection) is left as written (wrapping a runner of the same directory into that directory would recurse), and the already-done state is recognized by the runner marker and not rewritten; the layer is entirely optional: `wsl.mode` = on/off/auto and the two sync switches `wsl.syncClaude`/`wsl.syncCodex` (both off — the layer reports off); the distro list is `wsl.distros` (name and user), the in-distro paths are `wsl.startupScriptPath` and `wsl.proxyEnvPath`; the WinINET system proxy on/off. Every synced address is the applications proxy endpoint (AppProxyEndpoint — the local forward proxy's, or the external one when the local proxy is skipped; the distros reach a host-local endpoint through the WSL NAT gateway (recomputed at every shell start) and a routable external one at its own address, baked into the startup script and the Codex env snippet; the WSL status then warns read-only when that endpoint is loopback-only, hence unreachable from the distros — the upstream must listen on the WSL-facing address itself, the app creates no firewall rule for it). The status of all guards is read-only (a GUI card, `pnr guard status [--live]`); the ban and the policy go through an elevated helper, and after a ban — live probes (DNS/direct/proxied) | `PrivateNetRouting.Core.Guard` + the "Guards" card in App; CLI `pnr guard status [--live] \| ban on\|off \| policy on\|off \| settings sync \| wsl sync \| sysproxy on\|off` |
| App self-update | a one-time check on GUI start (`ui.checkAppUpdatesOnStart`) and a "Check for updates…" item of the "?" help menu: the repository's GitHub releases against the running version; a newer release — an offer to open the releases page (no auto-install); the request is anonymous api.github.com (the repository is public), no chain and no proxy needed; at start a failure is logged quietly, from the menu it is a message box | `PrivateNetRouting.Core.AppUpdate` + the "?" menu item; log `appupdate.log` |
| Browser profile | two phases: creation and launch. **Creation** — once, through the dialog ("?" help menu → "Create browser profile…"): the family (firefox/chrome), the profile folder (the system folder picker) and the browser executable (the standard places are probed first, any file can be pointed at by hand), plus an optional shortcut with an editable path (the desktop default is suggested, a missing parent folder is created; the checkbox is "Create a shortcut" — the path decides where it lands). The dialog writes the profile files — Firefox: `user.js` (all protocols through the browser egress from `browser.egress` with the names resolved on the egress side: the forward-proxy egress is written as http/ssl prefs of the application's CONNECT proxy, the chain egress as SOCKS5 with `socks_remote_dns` (the http mode's chain egress is an HTTP proxy itself — http/ssl prefs too); `no_proxies_on` from the `bypassDomains` domains in both forms + `*.tld` from `directTlds`, `failover_direct=false` — no silent direct fallback, `trr.mode=5`, the tabs of the last session back on start (`browser.startup.page=3`), no startup offers (welcome/upgrade dialog/what's-new/default-browser prompt/telemetry), WebRTC off (`media.peerconnection.enabled`/`media.navigator.enabled`) and the built-in VPN off; a fresh profile also gets a one-time `prefs.js` seed accepting the Terms-of-Use page — written only when no `prefs.js` exists yet and never into user.js, which would pin the value forever (verified live on Firefox 157)); Chromium: the WebRTC policy merged into `Default/Preferences` (`webrtc.ip_handling_policy` — the family's only lever, its command-line switch is dropped by current builds and a full off switch never existed there; the keys of an existing file survive) — and records the profile in `browser.*`. **The traffic goes through the browser egress chosen by `browser.egress`**: "forward-proxy" (default) — the applications proxy endpoint (AppProxyEndpoint) over HTTP CONNECT: names ride the CONNECT and are resolved on the proxy side, past the local ban; in the default shape that is the local forward proxy (`forwardProxy.listen`) and the proxy process (the GUI in the tray or the detached worker) has to be running, in a skipped shape (the http mode, or socks5 with `forwardProxy.enabled=false`) it is the external endpoint and nothing of ours has to run. "chain" — the chain's final egress (`chain.tailscale.socksListen` in the tailscale mode, the external upstream in the other modes: `chain.socks5.upstream` as SOCKS5, `chain.http.upstream` as HTTP CONNECT): the browser works independently of the application process (the desktop shortcut needs no PNR; the chain, however, has to be up), but the tailscale sidecar resolves names with the system resolver, so the names blocked from direct access do not open through it — such a launch warns in the log. **Launch** (the "Browser" button, `--launch-browser`, `pnr browser`) is only a start: a TCP probe of the egress (`browser.probeTimeoutSeconds`), then the browser; on a dead egress a question — when the egress is ours to raise (the forward proxy while the chain runs it, the sidecar's listener in the tailscale mode — never an external upstream) yes raises the chain (drawn in the chain card) and starts; over an external endpoint — the upstream with `browser.egress=chain`, or the skipped-shape egress — there is nothing to raise (the question is only whether to start anyway; a refusal is `Cancelled`). Firefox's `user.js` is refreshed on every application launch (the egress address and the bypass list stay fresh for the button and the shortcut alike); Chromium carries everything on the command line (`--proxy-server=<scheme>://`, the bypass list, `--user-data-dir`, `--no-first-run`, `--no-default-browser-check`, `--restore-last-session` — its session preference is MAC-protected against file edits, hence the flag; WebRTC is left to the profile policy alone — the family's command-line switch is gone from current builds, verified live on Chrome 155: the policy alone zeroes out ICE candidates, a profile without it exposes a host candidate); `--private`/`--url` come only from the CLI flags — the launch opens no URL of its own; a first click without a profile — a warning with the offer to create one; an already running instance of ours (cmdline with our profile) is not duplicated; only the profile is touched — never the browser's system settings or the everyday profile | `PrivateNetRouting.Core.Launcher.BrowserProfile` (`BrowserEgress`, `BrowserProfileFiles`, `BrowserProfileCommandLine`, `BrowserExecutableLocator`, `BrowserProfileService`; `IShortcutFactory`/`WScriptShortcutFactory` in SystemOps) + `BrowserLauncher` (probe → start); `BrowserProfileWindow` in App; CLI `pnr browser [--check-only] [--private] [--yes] [--url <url>]`; the "Browser" button in the main window; GUI shortcut mode `--launch-browser`; the "Claude (browser)" shortcuts point at the application |
| Diagnostics | a read-only state summary: owners of the chain listeners with PIDs (a TCP table through iphlpapi, no PowerShell), keeper/node — or, in an external-upstream mode (socks5, http), one egress line (the upstream, the socks5 one with its user and never the password, and its reachability), the proxy's WSL bind (flagged when it is not the current gateway address), counters and age of the activity marker, a detailed CONNECT roundtrip (time + first response line), the core of the guards (policy + ban, without WSL), idle stopper liveness (visible from outside by the command line: the detached worker `--watchdog-worker` or the foreground `pnr watchdog`); a fully stopped chain — a short "normal" message instead of the full picture (in the external modes the daemon ports mean nothing and are not consulted; in a skipped shape the forward-proxy port is not consulted either and the summary says "skipped — the applications point at …", with the CONNECT roundtrip running against the upstream); log viewing (a list with sizes, tail by name `--tail N`) and rotation: the process's live log — in place (`FileLog.RotateNow`), a free file — by shifting the age chain, one held by another process — honestly "locked" | `PrivateNetRouting.Core.Diagnostics` + `DiagnosticsWindow` in App (the "Diagnostics…" item of the "?" help menu: a highlighted summary and an "Open logs" button — the log list and rotation live in the CLI); CLI `pnr status`, `pnr logs list \| show <name> [--tail N] \| rotate [<name>...]` |
| Localization | UI multilingual support: strings are in the `Strings.<code>.xaml` dictionaries (`{DynamicResource}` in XAML, `Loc.S`/`Loc.F` in code), the dictionary is swapped on the fly; the language is `ui.language` in the config: `auto` (system language) or a code, an unsupported one — English; a new language = a dictionary + a code in `AppLanguage.Supported` | `PrivateNetRouting.App/Localization`; en/ru; a flag selector (en/ru, drawn by `LanguageFlag`) in the window header — no "system" option: the config's `auto` is shown as the language it resolves to, a manual choice writes the concrete code to the config |

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
  (`ForwardProxySupervisor.DetachAsync`), and so do the CLI commands that leave a client behind
  (`pnr launch`, `pnr codex launch`, `pnr browser`, `pnr chain up`) before they exit — a failed
  handover is a non-zero exit; the idle
  stopper started from the GUI — a separate worker (`--watchdog-worker`) that outlives the exit on
  its own and disappears after the idle stop (the CLI form `pnr watchdog` — foreground, in the
  terminal).
- Logs: one canonical file per log — all writers (the GUI in the tray + `pnr` from a terminal,
  detached workers) open the same `<name>.log` with FileShare.ReadWrite and seek to the end
  before every line; rotation is by size — at start and before the next record into an overgrown
  file (a chain held by another process postpones the rotation until the owner's RotateNow or the
  next start), manually — from diagnostics (the live log of the process — in place, a free file —
  by shifting, a held one — honestly "locked"); a shift stages the current file under a name
  unique per rotation (`<log>.rotating-<pid>-<random>`), and a staging file left by a crashed or
  failed rotation is never overwritten: the next rotation (or the next start) appends it to the
  end of `.1`, so no record is lost.
- Updater: the version check phase runs before the chain bring-up (no extra UAC is needed);
  installation is not interrupted; during long phases keep the activity marker fresh so that
  auto-stop does not shut the chain down under the download.
- Releases and installation: `Directory.Build.props` holds the version and must match the release
  tag (the workflow checks); a `v*` tag runs the release workflow — tests, then
  `packaging/make-release.ps1` (both projects self-contained into one folder, the portable zip,
  the Inno Setup installer) — and attaches zip + setup to a GitHub release with generated notes.
  The installer is per-user (`%LOCALAPPDATA%\Programs\PrivateNetRouting`, no elevation), recognizes
  the running GUI by the single-instance mutex, terminates the background workers before touching
  the files (they have no window, so the Restart Manager cannot close them — the install and the
  uninstall would stop on the "unable to automatically close all applications" error), and writes
  no autostart entries (rule #4). The uninstaller reverts nothing outside its folder: it asks first,
  listing the commands that turn the guards off (`pnr guard ban|policy|sysproxy off`, `pnr chain down`).
- Guard: the ban rule identifiers (hosts block markers, the NRPT rule DisplayName, the firewall
  rule Group) are in the config; the application maintains a ban with these identifiers, not a
  parallel one. The guard status is read-only; after applying or removing a ban — live probes,
  not "it should work".
- The external protocols the semantics rest on: CONNECT roundtrip as a health check, the WSL host's
  `server.shutdown` RPC, Claude's update feed (`claude.update.updateFeedUrl`, the same endpoint the
  built-in updater polls) as the primary source of the package URL with the AppX journal as the
  fallback, AppX Publisher verification.
- UI tokens: everything visual comes from `App.xaml` — semantic brush names (window/band/card/code,
  three weights of text, two brand accents — Claude coral and Codex/OpenAI green —, the state
  colours), a 4px spacing grid, two corner radii and a
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
                             # the language selector with drawn flag marks (en/ru; the config's
                             # "auto" shows as the resolved language, a manual choice writes the
                             # code to the config; LanguageFlag + the data template) and the "?"
                             # help menu (diagnostics, open logs, create browser profile,
                             # check for updates, about with the project's GitHub page),
                             # the chain card — the single lifecycle control
                             # (idempotent bring-up/stop of the whole chain, the animated rail
                             # that runs a chain operation live — a bring-up (the card's or a
                             # launch's), or a stop, whose stage words flip to stopping/stopped
                             # and whose finished stages repaint neutral; a chain snapshot
                             # taken while an operation runs never overwrites the rail,
                             # during a bring-up (own or a launch's) Stop acts as cancel — the
                             # operation aborts, what got raised is extinguished (a launch
                             # extinguishes only the chain it raised itself),
                             # status rows: the PNR listeners with connection counters, the
                             # upstream row (tailscale listeners, or the socks5 egress with its
                             # reachability word), the config with its actions (open the
                             # folder / reload; a reload that changed the running proxy's
                             # section says an application restart applies it), the idle
                             # stopper with its status, the
                             # auto-start flag and the toggle; collapsible to one compact row —
                             # overall status dot + word and the same two buttons, a chain
                             # operation expands the card and collapses it back on success,
                             # the choice is remembered in ui.chainCardExpanded),
                             # the --launch shortcut mode
                             # (Claude running → window only), the "Browser" button and the
                             # --launch-browser shortcut mode (the profile created once by the
                             # dialog, egress probe before the start, "already running" note),
                             # the launch actions in one row — the Claude pair pinned
                             # left, the shared "Browser" button centred (it leans to the freed
                             # side: without Codex — the right edge, without Claude — the left),
                             # the Codex pair pinned
                             # right (Launch Codex in the OpenAI green, CodexButton; an app off
                             # in the config — claude.enabled/codex.enabled — takes its pair out, its
                             # tray launch item and its startup update check with it), each
                             # update button carries its app's update dot,
                             # the startup Claude and Codex update checks, the update windows
                             # (UpdateWindow for Claude, CodexUpdateWindow for the Store update;
                             # the "Guards" card (status and control of
                             # ban/policy/settings.json/WSL; collapsible to one row — aggregate
                             # status dot + word and a single combined action: "enable all"
                             # = ban + policy + both syncs, "disable all" = ban + policy;
                             # ban and policy go as one elevation (--elevated-guard-all-on/off),
                             # remembered in ui.guardsCardExpanded), the diagnostics window
                             # (DiagnosticsWindow: read-only state summary with highlighting,
                             # the logs-folder button);
                             # the shared application icon
                             # (AppIcons + Assets/app.ico, the tray is a WinForms NotifyIcon);
                             # the windowless roles (the detached proxy worker, the idle stopper,
                             # the elevated helper commands) run from Program.Main through
                             # HeadlessHost, before WPF starts — no Application, no dispatcher; the
                             # helper gets the asking user's config through --config
    LanguageFlag.cs          # the language selector's option data (LanguageOption) and the drawn
                             # mark of a language: the Union Jack / three stripes
    Localization/            # string dictionaries Strings.<code>.xaml + Loc: on-the-fly dictionary
                             # swap (DynamicResource in XAML, Loc.S/Loc.F in code,
                             # Loc.LanguageChanged)
    ChainRail.cs             # the chain rail of the main window: the mode's stages as markers on a
                             # link (tailscale: five, socks5: three — the label keys come in through
                             # the constructor), built in code so a stage can light up (colour
                             # fade, a pop when it completes, a breath while it is being brought up)
    StepRail.cs              # one row of a vertical rail (the update window's phases), sharing the
                             # marker vocabulary of the chain rail: hollow / check / cross
    Theme.cs                 # App.xaml tokens for the code paths: state → brush, state → word
    WindowTheme.cs           # dark DWM title bar; unsupported builds keep the system one
  PrivateNetRouting.Core/    # logic without UI
    Configuration/           # AppConfig (including claude.enabled/codex.enabled — an app off hides its
                             # UI pair, its tray item and its startup update check, and its pnr
                             # commands refuse with a note; ui.language, ui.checkUpdatesOnStart,
                             # ui.chainCardExpanded, ui.guardsCardExpanded),
                             # AppLanguage (auto → system → en),
                             # loading from %APPDATA% (the template is config/*.example.json)
    Logging/                 # FileLog: DEBUG..ERROR levels (logging.level, default info); one
                             # shared file per log (no PID fallback); size rotation at start and
                             # during writes (postponed while the chain is held) + manual RotateNow
    AppUpdate/               # app self-update: AppUpdateChecker (the running version vs the
                             # latest release of the repository) + GitHubAppReleaseFeed
                             # (anonymous api.github.com — the repository is public); the
                             # "?"-menu item and the one-time start check
                             # (ui.checkAppUpdatesOnStart, the offer opens the releases page)
    Net/Dns/                 # DnsWire (RFC 1035/8484), DohClient (plain http and https over the
                             # upstream), DohEndpointPool, cache resolver,
                             # TailscaleNetmapDohSource (auto-filling the pool from the tailnet
                             # config: any DoH resolvers — http/https, by IP or by name)
    Net/Socks/               # Socks5Client (no-auth or user/pass; dial by IPv4 or by name, ATYP=1/3)
    Net/WslGateway.cs        # WSL NAT gateway address (vEthernet (WSL...)) for the proxy bind (re-read, the bind follows a recreated WSL network)
    Net/Proxy/               # ForwardProxyService (CONNECT proxy), parser, ProxyHealthCheck
                             # (CheckAsync-bool + ProbeAsync with timing and a status line),
                             # ForwardProxySupervisor (listener placement: in-process or a detached
                             # worker, the port is a single resource), ProxyWorker (the body of
                             # --proxy-worker for pnr and App)
    Chain/                   # ChainSupervisor (orchestration), SidecarDaemon (direct or elevated
                             # start by chain.tailscale.elevation, stale kill), TailscaleVersionProbe
                             # (the auto elevation decision), SidecarKeeper (watch-ipn),
                             # SidecarCli, ElevatedChain (runas helper)
    Launcher/                # ClaudeLauncher (bypass list, chain bring-up on failure, launch with
                             # proxy flags, warnings about the policy/running Claude);
                             # CodexLauncher (the Codex desktop app: package-context launch with
                             # proxy flags + env, already-running warning);
                             # RunningApp (an already running copy: the state scan, the log report,
                             # the graceful/forced close of the restart);
                             # BrowserProfile (the profile created once: files of both families,
                             # the command line shared by the button and the shortcut, the
                             # executable lookup, the config write-back) and BrowserLauncher
                             # (egress probe → start; "already running" by the profile in cmdline)
    Updater/                 # ClaudeUpdater (MSIX update orchestration: check → chain →
                             # download → Publisher verification → install), PowerShellRunner,
                             # PowerShellAppxPackageSource (Get-AppxPackage/Add-AppxPackage),
                             # UpdateFeedUrlFinder (address from Claude's update feed through the
                             # live proxy) + PowerShellJournalUrlFinder + PackageUrl (address from
                             # the AppX journal),
                             # CodexUpdater + StoreUpdateSource (the Store update: search by
                             # product id, start by package family name, status polling)
                             # + CodexVersionManifest (OpenAI's offered-version manifest through
                             # the live proxy),
                             # FragmentDownloader (chunked download with retries and resume),
                             # MsixPackage (Identity/Publisher from AppxManifest.xml)
    Guard/                   # direct-access control: EgressPolicyStore (HKLM Policies\Claude),
                             # HostsFileBlock (idempotent hosts block), PowerShellBanSystemOps
                             # (NRPT + firewall via cmdlets), ClaudeSettingsGuard (env in settings.json),
                             # CodexSettingsGuard (the respect_system_proxy flag in config.toml of
                             # Codex plus the proxy env in CODEX_HOME/.env — surgical edits of
                             # files owned by the app; CodexEnvFile holds the .env merge),
                             # WslConfigGuard (the distro boot layer — the [boot] command of wsl.conf
                             # → the marked runner of the startup directory, a foreign command
                             # preserved as 00-…; startup script + settings.json of the distros; the
                             # layer is optional: mode on/off/auto, sync per distro),
                             # WinInetSystemProxyStore + system proxy bypass, GuardService
                             # (status/operations/live probes), GuardElevatedCommands (UAC helper)
    Watchdog/                # idle auto-stop: ClaudeUsageProbe (processes/connections/marker),
                             # WslAppHostStopper (stopping the app hosts of the running configured distros:
                             # Claude RPC + signal escalation, Codex by CODEX_HOME in the env),
                             # WatchdogService (the loop, WSL → chain),
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
                             # launch [--check-only] [--no-splash] [--restart] |
                             # browser [--check-only] [--private] [--yes] [--url <url>] |
                             # codex launch [--check-only] [--restart] | update [--check] ... |
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
                             # profile on fakes (user.js of both families/the Chromium policy
                             # merge/command lines/the shortcut service) and the browser launcher
                             # on fakes (bypass lists/egress probe/dead-egress contract/path
                             # fallback/"already running"), the Codex launcher/updater on fakes
                             # (launch script/launch order/Store search/update polling/version
                             # manifest/codex activity probe), the watchdog on fakes
                             # (WSL→chain order, the stopper output parser, the activity probe),
                             # the idle stopper supervisor on fakes (liveness by command lines,
                             # idempotent start, kill of the identified stoppers only),
                             # the guard on fakes (hosts block, settings.json env, config.toml flag of Codex:
                             # add/insert/rewrite/dotted/fresh file, the .env merge: fresh/replace/
                             # export-form/foreign lines/idempotence, NRPT/firewall
                             # scripts, the WSL layer: modes/status/sync, orchestration:
                             # layers/elevation/live probes), the updater on fakes (versions/journal
                             # URL, chunked download on a fake HTTP: resume/retries/cancellation,
                             # Publisher, closing Claude, the full cycle), diagnostics on fakes
                             # (summary: stopped chain/port owners/WSL bind/health/guards/watchdog,
                             # the core of the guards without WSL, ProbeAsync on a live TcpListener),
                             # logs (list/tail/rotation/busy file/shared single file/size
                             # rotation during writes/postponed rotation/RotateNow), the cmd argument form
config/
  appsettings.example.json   # user config template with placeholders
packaging/
  setup.iss                  # Inno Setup script: per-user install (no elevation), the version
                             # via /DAppVersion, no autostart (rule #4)
  make-release.ps1           # self-contained publish of App+Cli + zip + installer into
                             # artifacts/; the release workflow runs the same script
.github/
  workflows/release.yml      # a v* tag: tests → make-release.ps1 → a GitHub release with the
                             # zip and the installer (the tag must match Directory.Build.props)
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
| gh | 2.102 | logged in; the public repository on github.com |

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
focus"). No attribution trailers: a commit message never gets Co-Authored-By or "Generated with …"
lines (rule #2 — the repository stays anonymous; the Claude Code setting is
`includeCoAuthoredBy: false`). Branching: `main` is the buildable state; features go in short
branches or straight into `main` (a personal repository, the main thing is not to break the build).

## Code conventions

- C# 14 / .NET 10; every project targets `net10.0-windows10.0.19041.0` (Core and the CLI need
  Windows APIs —
  WMI, iphlpapi), so the whole solution is Windows-only.
- `Directory.Build.props` sets `Nullable=enable`, `ImplicitUsings=enable` and
  `TreatWarningsAsErrors=true`: any compiler warning fails `dotnet build`. Fix the warning; do not
  silence it with `#pragma warning disable` or `NoWarn`. The WPF app is `WinExe`, the CLI's
  `AssemblyName` is `pnr`, and Core exposes internals to the test project (`InternalsVisibleTo`).
  Keep `Version`/`Company`/`Authors` as the anonymous placeholders they are (rule #2) — never fill
  in real author metadata.
- Core's only external package is `System.Management` (WMI process lookup). Prefer the BCL and
  `SystemOps` wrappers; adding a dependency needs a reason.
- Line endings: the repository is pinned to LF by `.gitattributes` (`* text=auto eol=lf`); keep it
  that way. Raw multiline C# string literals (`"""`) take their line endings from the checkout — a
  CRLF checkout (a Windows CI runner, a clone with `autocrlf=true`) silently puts `\r` into every
  script built from them, and bash inside a WSL distro dies on the first line while `wsl.exe`
  still exits 0. Everything a Core builder sends into bash or embeds into a distro file must be
  LF-normalized at the boundary (`WslConfigGuard.NormalizeLf`/`DistroPayload`; the guard test
  asserts LF-only scripts and payloads) — same rule for any new WSL-bound script.
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
- `docs/claude-proxy.md`, `docs/codex-proxy.md` — the sanitized hands-on guides: putting Claude
  Desktop/Claude Code and the Codex desktop app through the local forward proxy, on Windows and
  inside WSL (mechanics only, placeholders instead of environment values).
- `docs/testing.md` — the hands-on testing guide: GUI automation on a locked session (UIA +
  PrintWindow recipes, what works unattended and what does not) and the socks5-for-tailnet
  test setup; read it before a test session instead of rediscovering the methods.
- `.zcodeignore` — the block above the sync marker is rewritten from `.gitignore`; put agent-only
  rules below the marker line, or they will be lost on the next sync.
- `.zcode/` is git-ignored (agent workspace state: skills, plans); it holds no tracked files. Keep
  scratch files out of commits all the same.

## Working in the shell (environment quirks, briefly)

The tool shell is Git Bash; PowerShell is invoked as `pwsh -NoProfile -File <script.ps1>` (for long
scripts) or `pwsh -NoProfile -Command '…'` in bash single quotes (for short ones; never in double
quotes — bash would expand `$`). Cyrillic in the output of PS/native utilities — run
`chcp 65001 >/dev/null` first. Do not execute PS code in bash and do not grep-parse `.ps1` — only
run it. These quirks belong to the shell environment, not to the application.