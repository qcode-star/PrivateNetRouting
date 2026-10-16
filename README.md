# PrivateNetRouting

A personal Windows toolkit (WPF, .NET 10 LTS) that runs Claude Desktop and the Codex desktop app
(OpenAI) through a private proxy chain: a local forward proxy with DoH resolution, a chain
supervisor, launchers for both apps, a browser profile of its own, idle auto-stop, an MSIX
package updater for Claude and a Microsoft Store updater for Codex, direct-access guards, and
read-only diagnostics. It provides a GUI and a headless CLI. Development rules are in
[AGENTS.md](AGENTS.md). Hands-on guides: [running Claude through the proxy](docs/claude-proxy.md)
and [running the Codex desktop app through the proxy](docs/codex-proxy.md) — Windows and WSL.

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

## Typical setups

The shapes the tool covers, and where the forward proxy is load-bearing in each. Everything else
in the config (bypass domains, guards, watchdog) is orthogonal to the choice and stays as in any
other shape.

**1. Managed tailscale (the default shape).** The application raises its own userspace
sidecar; the forward proxy is load-bearing for everyone: it resolves the AI names over DoH
(the sidecar's own proxies resolve with the system resolver, which the local ban blackholes)
and dials by IP through the sidecar. Claude and Codex speak HTTP CONNECT to it, the browser
profile points at it by default (`browser.egress=forward-proxy`), and the WSL path rides its
bind on the WSL NAT gateway. The most self-contained and controllable shape — preferred over
the ones below: nothing outside the application's own processes matters.

```jsonc
"chain": { "mode": "tailscale" },            // the default
"browser": { "egress": "forward-proxy" },    // the default
"forwardProxy": { "enabled": true },         // the default
// wsl: the distros to sync into — the name as in `wsl --list`, nothing is auto-discovered
"wsl": { "distros": [ { "name": "<distro-name>", "user": "<distro-user>" } ] }
```

The first raise with a fresh statedir goes through the interactive tailnet login: the sidecar
node has no key yet, so the application runs `tailscale login` against its own sidecar, opens
the authentication page in the browser (the URL is also in the chain card's step line and in
the log) and continues the bring-up once the device is approved. The coordination server comes
from `chain.tailscale.loginServer`; when it is empty, the machine's system-wide tailscale is
asked for its own (a read-only read of its prefs — the system service only has to answer at
that moment) and the node state remembers the answer afterwards: every later `tailscale up`
mentions the control server the node is bound to (tailscale rejects an up that leaves a
non-default one out). The login is one-time — the
key lives in the statedir (`chain.tailscale.loginWaitSeconds` bounds the wait). Migrating from
a system-wide tailscale: disable the service's autostart (do not uninstall — the application
uses its binaries) and raise the chain; the sidecar node is a separate identity and logs in
anew.

**2. A tailscale outside the application (system service or user-run) + the socks5 chain.**
The upstream is reached through that tailscale, so the application's sidecar is not needed.
The forward proxy still translates HTTP CONNECT for the applications and bridges WSL (the
gateway bind); for the browser it depends on `browser.egress` — the proxy by default, or
straight to the upstream's SOCKS5 with `chain`. Neither egress resolves a name on this
machine: the names ride the CONNECT/SOCKS5 to the upstream, and the upstream's own
resolution decides. When the upstream is that tailscale's own SOCKS5, a userspace-mode
tailscale resolves with the system resolver — the verified limitation the forward proxy
exists for in shape 1 — and the local ban blackholes the AI names exactly there: no egress
choice helps, the proxied AI names do not open. A system-service tailscale answers as its
own DNS configuration (accept-dns / split DNS) allows, and everything going direct — bypass
zones included — follows it as well: one more reason shape 1 is preferred and more
controllable (there the proxy resolves over DoH itself and dials the sidecar by IP).

```jsonc
"chain": {
  "mode": "socks5",
  "socks5": { "upstream": "<upstream-ip>:<port>" }, // an IP literal — a host name is rejected;
                                                      // reached through the outer tailscale;
                                                      // user/password if the upstream asks
},
"forwardProxy": { "enabled": true },          // the default
"browser": { "egress": "forward-proxy" },     // or "chain" — no DNS difference here: the
                                              // upstream resolves the names in either case
// wsl: the distros to sync into — the name as in `wsl --list`, nothing is auto-discovered
"wsl": { "distros": [ { "name": "<distro-name>", "user": "<distro-user>" } ] }
```

**3. sing-box (mixed inbound) + socks5 chain, no WSL.** The forward proxy is not needed
(`forwardProxy.enabled=false`): every client is pointed at `chain.socks5.upstream` itself —
that one address must then answer HTTP CONNECT as well as SOCKS5, which is exactly what a
mixed inbound does on one port.

```jsonc
"chain": {
  "mode": "socks5",
  "socks5": { "upstream": "127.0.0.1:2080" },  // the sing-box mixed inbound — both protocols on one port
},
"forwardProxy": { "enabled": false },
"browser": { "egress": "chain" }               // straight to the mixed inbound (SOCKS5)
// wsl stays off — there is nothing to bridge
```

**4. A provided HTTP proxy + http chain.** An externally provided plain HTTP proxy (CONNECT-capable,
no SOCKS of its own). The local proxy has nothing to add here — the upstream speaks HTTP CONNECT
itself and resolves the names carried by the CONNECT on its side, so the mode runs no local proxy
at all (`forwardProxy.enabled` is ignored): every client — the apps' flags and env, the managed
policy, the WSL sync, the updater, the checks — is pointed at `chain.http.upstream`. No SOCKS5
exists anywhere in this shape; no UAC, no tailscale processes, and the egress check is the CONNECT
roundtrip against the upstream itself.

```jsonc
"chain": {
  "mode": "http",
  "http": { "upstream": "<proxy-ip>:<port>" },  // the provided HTTP proxy (CONNECT), an IP literal
},
"forwardProxy": { "enabled": false },            // irrelevant in this mode — the proxy never runs
"browser": { "egress": "forward-proxy" },        // or "chain" — both resolve to the same upstream
// wsl: the distros to sync into — the name as in `wsl --list`, nothing is auto-discovered
"wsl": { "distros": [ { "name": "<distro-name>", "user": "<distro-user>" } ] }
```

**5. sing-box (mixed inbound) + socks5 chain + WSL.** The forward proxy is the simple,
ready way to bridge WSL: its bind on the WSL NAT gateway keeps sing-box on loopback — no
`0.0.0.0` exposure, no firewall work of the owner's own.

```jsonc
"chain": {
  "mode": "socks5",
  "socks5": { "upstream": "127.0.0.1:2080" },
},
"forwardProxy": { "enabled": true, "bindWslGateway": true },  // the defaults; the proxy stays: it is the WSL bridge
"browser": { "egress": "chain" },             // or "forward-proxy" — both reach sing-box
// wsl: the distros to sync into — the name as in `wsl --list`, nothing is auto-discovered
"wsl": { "distros": [ { "name": "<distro-name>", "user": "<distro-user>" } ] }
// sing-box keeps listening on loopback
```

**6. A plain SOCKS5 upstream (no HTTP inbound) + socks5 chain.** The forward proxy is the
HTTP translator for the clients that have no SOCKS5 of their own — Claude and Codex, whose
managed policy and proxy env speak HTTP CONNECT only. The browser is not one of them: both
families speak SOCKS5 natively (with remote DNS), so `browser.egress=chain` reaches the
upstream directly — riding the translator is the default setting, not a necessity. Skipping
the proxy (`forwardProxy.enabled=false`) is not an option here: every client would be pointed
at `chain.socks5.upstream` itself, and a plain SOCKS5 upstream cannot answer HTTP CONNECT.

```jsonc
"chain": {
  "mode": "socks5",
  "socks5": { "upstream": "<upstream-ip>:<port>" },
},
"forwardProxy": { "enabled": true },          // required: the apps have no SOCKS5 of their own
"browser": { "egress": "chain" },             // recommended: the browser speaks SOCKS5 natively
// wsl: the distros to sync into — the name as in `wsl --list`, nothing is auto-discovered
"wsl": { "distros": [ { "name": "<distro-name>", "user": "<distro-user>" } ] }
```

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
(the browser profile created once by the dialog).

The headless CLI (`pnr`):

```bash
pnr proxy [--detached]         # forward proxy in the console, or as a detached worker
pnr chain status|up|down       # chain state / bring up / stop
pnr check                      # CONNECT roundtrip health check
pnr status                     # read-only diagnostics summary
pnr logs list|show|rotate      # log viewing and rotation
pnr launch [--check-only] [--no-splash] [--restart]
                               # start Claude Desktop through the proxy (--restart closes a copy
                               # already running without the proxy and starts it again)
pnr browser [--check-only] [--private] [--yes] [--url <url>]
                               # start the browser with its profile (created through the "?" menu);
                               # the profile goes through the egress from browser.egress — with the
                               # default forward-proxy the proxy must be running (it comes up with
                               # the chain)
pnr codex launch [--check-only] [--restart]
                               # start the Codex desktop app through the proxy
pnr codex update [--check]     # update the app through the Microsoft Store
pnr update [--check] ...       # manual MSIX update
pnr watchdog                   # idle auto-stop supervisor
pnr guard status [--live]      # guards: read-only status; ban/policy/settings/wsl/sysproxy to change
pnr config path|init           # user config path / create from defaults
```

## Configuration

Everything environment-specific (endpoint addresses, ports, bypass domain lists, paths, timeouts)
lives in the application's user config and never reaches git. The repository holds only templates
with placeholders in `config/*.example.json`. An installation that uses only one of the two apps
turns the other off in the config (`claude.enabled` / `codex.enabled`): its launch/update buttons,
its tray item and its startup update check disappear, and its `pnr` commands refuse with a note —
the guards, the chain and the idle stopper keep working. The repository is public and anonymous: no
real addresses, domains, names, or secrets (rule #2 in AGENTS.md).

The WSL layer (the guard sync into the distros and the idle-stop WSL step) works only over the
distros listed in `wsl.distros` in the user config — nothing is discovered automatically, the name
must match `wsl --list` exactly. With the default `wsl.mode=auto` the layer turns on when at least
one configured distro is installed; an empty list keeps it off.

## Modules

| Module | What it does |
|---|---|
| Forward proxy | Local CONNECT proxy. Why it exists: the tailscale sidecar's own proxies (SOCKS5 and HTTP) resolve names with the system resolver — their dialer never consults the netstack DNS — and the direct-access ban blackholes exactly the AI names there, so this proxy does the resolution itself and dials by IP; besides that, applications speak HTTP CONNECT (the managed policy of Claude, `HTTPS_PROXY` of Claude Code and Codex), which a plain SOCKS5 upstream does not answer. In the tailscale mode it resolves names over DoH (the resolvers are picked up from the tailnet netmap — http and https, by IP or by name; the optional `dohSeed` adds extras) and dials by IP through the SOCKS5 upstream — the sidecar's own listener (`chain.tailscale.socksListen`), so the address cannot drift from the daemon. In the socks5 mode the trusted external upstream from `chain.socks5.upstream` (optionally with username/password, RFC 1929) resolves on its side: the target name rides the CONNECT, no DoH involved. An upstream with a mixed HTTP+SOCKS inbound (e.g. sing-box) makes the local proxy redundant in the socks5 mode: `forwardProxy.enabled=false` runs no local proxy at all and every client is pointed at `chain.socks5.upstream` itself — the address must then answer HTTP CONNECT as well as SOCKS5, the mixed inbound does both on one port (the tailscale mode refuses the bring-up when the flag is off: there the proxy is load-bearing). The http mode goes one step further: the provided plain HTTP proxy from `chain.http.upstream` speaks CONNECT itself, so the local proxy never runs there at all and that address is the applications endpoint for everyone. Listens on localhost and the WSL gateway, keeps an activity marker for the watchdog. Runs in-process or as a detached worker that outlives the GUI/command; a stopped proxy starts again cleanly in the same process. |
| Chain supervisor | Three modes by `chain.mode` (a change applies after a restart). "tailscale" (default): idempotent bring-up/stop of the userspace tailscaled daemon → keeper → node → forward proxy; on tailscale 1.104.1+ the daemon runs without elevation (`chain.tailscale.elevation`, default auto), older builds start it through a one-shot elevated copy (UAC). The elevated copy executes the standard per-machine Tailscale install (`%ProgramFiles%\Tailscale`) regardless of `tailscaledPath`/`cliPath` (the user-writable config does not name what runs as administrator): its start refuses without that install and takes the dev flag from that binary's own version, its stop downs the node with the standard CLI when it is there and kills the sidecar processes either way. A node without a key (a fresh statedir, an expired login) goes through the interactive tailnet login first: the application runs `tailscale login`, the authentication URL rides the progress as soon as it appears — the GUI opens the page in the browser, the CLI prints it — and `chain.tailscale.loginWaitSeconds` bounds the wait; the control server is `chain.tailscale.loginServer`, or — when empty — the one picked up from the machine's system-wide tailscale (a read-only read of its prefs). "socks5": the chain is one check of the external SOCKS5 upstream from `chain.socks5.upstream` (a CONNECT-by-name roundtrip through it — the upstream resolves the name on its side) plus the forward proxy itself — no tailscale processes and no UAC; stopping takes down only the proxy. "http": the same shape against the provided plain HTTP proxy from `chain.http.upstream` — the upstream answers the CONNECT check natively, no local proxy ever runs there, no SOCKS5 exists anywhere in the shape; stopping has nothing local to stop. With `forwardProxy.enabled=false` the socks5 shape loses its proxy link too: egress check → health against `chain.socks5.upstream` itself. The chain card draws the mode's steps: three tailscale stages become one "Egress" stage (two stages — "Egress", "CONNECT check" — in a shape without the local proxy). While a bring-up is running (its own, or the one inside a launch), the card's Stop cancels it: what got raised is stopped, a chain raised before the launch keeps working. A chain stop runs the same rail the other way: the stage words flip to stopping/stopped and its finished stages repaint neutral; a chain snapshot taken while an operation runs never overwrites the picture. |
| Launchers | Claude Desktop with `--proxy-server`/`--proxy-bypass-list`; the Codex desktop app (OpenAI, Microsoft Store MSIX) with the same flags plus a refresh of the proxy env in `CODEX_HOME/.env` — the file its embedded CLI core loads itself (the app strips the inherited proxy env from its child processes; a package-context launch). Both bring the chain up when needed; the bring-up runs live in the chain card of the main window. Both desktop apps are single-instance, so a copy already running without our proxy would swallow a new start: the window shows a dialog and the CLI asks (or takes `--restart`) before closing that copy gracefully (a forced tree kill only for what survives the wait) and starting it again through the proxy. |
| Browser profile | Created once through the dialog ("?" help menu → "Create browser profile…"): the family (Firefox or Chrome), the profile folder, the browser executable (the standard places are probed first, any file can be pointed at by hand) and an optional shortcut (the path is editable; the desktop default is suggested). The traffic goes through the egress chosen by `browser.egress`. "forward-proxy" (default): the applications proxy endpoint over HTTP CONNECT — Firefox gets http/ssl prefs in its `user.js`, Chromium `--proxy-server=http://…` — the names ride the CONNECT and are resolved on the proxy side over DoH, so the local ban cannot poison them; the proxy process (the GUI in the tray or the detached worker) has to be running, or — when the local proxy does not run (the http mode, or the socks5 mode with `forwardProxy.enabled=false`) — the external upstream stands in for it and nothing of ours has to run. "chain": the chain's final egress (`chain.tailscale.socksListen` in the tailscale mode, the external upstream in the other modes — SOCKS5 for the socks5 one, HTTP CONNECT for the http one) — the browser then needs no PNR process (the chain itself still has to be up), but the tailscale sidecar resolves names with the system resolver, so the names blocked from direct access do not open through it (such a launch warns in the log). Inside the profile WebRTC is off (Firefox fully — `media.peerconnection.enabled`/`media.navigator.enabled`; Chromium through `webrtc.ip_handling_policy`, the only lever that family has — its command-line switch is gone from current builds, and the policy alone was verified to zero out ICE candidates), the tabs of the last session come back on start, the startup offers are off — the fresh profile's welcome/terms page included (a one-time `prefs.js` seed) — and the configured local zones stay on the direct route. The "Browser" button, `--launch-browser` and `pnr browser` only probe the egress (a TCP check, `browser.probeTimeoutSeconds`) and start the browser; a dead egress that our chain brings up is offered a chain raise right there (over an external endpoint — the socks5 upstream, or the skipped-shape egress — the question is only whether to start anyway). Firefox's `user.js` is refreshed on every launch from the application — the button and the shortcut read the same file; Chrome carries the proxy on the command line. |
| Watchdog | Idle auto-stop: watches Claude and Codex processes, connections to the applications proxy endpoint and to the chain's egress listener (a browser pointed there by `browser.egress=chain` goes straight there, so browsing keeps the chain up; the default forward-proxy egress is covered by the proxy-connection check), and the activity marker — the marker signal is skipped when the local proxy does not run (a skipped shape — the http mode, or socks5 with `forwardProxy.enabled=false`: nobody touches the marker, the connections remain); on idle it stops the app hosts inside the RUNNING configured WSL distros (`wsl.distros` — no distro is booted just for the check; Claude's remote host is shut down by RPC, leftover Codex processes are recognized by `CODEX_HOME` in their environment) and then the chain. Lives only over a live chain: every completed chain stop takes the stopper down with it, and it cannot be armed over a stopped chain (the GUI Start is disabled, `pnr watchdog` refuses). Started explicitly (the toggle in the chain card spawns a detached background worker that keeps working after the application is closed and switches itself off after firing, or run `pnr watchdog` in a terminal). The auto-start flag (on by default) re-arms the stopper after every chain bring-up. |
| Updater | Manual MSIX update of Claude Desktop: version check before bring-up, package URL from Claude's own update feed (asked through the already-live proxy) with the AppX journal as the fallback, multi-threaded chunked download through the proxy, Publisher verification, `Add-AppxPackage`. When no source has a package address, the update window offers to raise the chain and asks the sources again over the live chain. A startup check announces an update only with a concrete offered version and lights an accent dot on the update button (the versions in its tooltip), which the update window's own check or a finished installation clears; the window closes itself 30 s after a successful installation, with a visible countdown. The Codex desktop app updates through the Microsoft Store instead: the Store catalog is asked by the product id (no chain, no UAC), OpenAI's version manifest names the offered version (through the live proxy), the update hands the package to the Store and follows its progress, closing a running Codex first. |
| Guards | Direct-access control: the managed `egressProxyUrl` policy, a local ban (hosts + one NRPT rule + firewall subnet block), the application settings — one sync writes the Claude Code env and the Codex layer — the `[features] respect_system_proxy = true` flag into config.toml plus the proxy env into the `.env` of the same CODEX_HOME, the file the embedded CLI core loads itself (surgical edits: every other line of the files Codex owns survives, backups are kept) — plus Claude Code env in WSL and, for the Codex CLI inside a distro, the proxy env layer — a profile snippet that exports the application proxy only to the shells Codex itself starts (the desktop app passes `CODEX_HOME` there for its own processes, the codex command name is the fallback) and rewrites that CODEX_HOME `.env` to the current gateway address, recomputing the WSL NAT gateway address on every shell start, while ordinary shells, apt, pip and git stay direct — plus the distro boot layer: the sync points the `[boot]` command of `/etc/wsl.conf` at a marked runner that executes the startup directory in order, preserving a foreign command as its first script — and the WinINET system proxy. Every synced address is the applications proxy endpoint — the local forward proxy's, or the external upstream when the local proxy does not run (the http mode, or the socks5 mode with `forwardProxy.enabled=false`) (the WSL status then warns when that address is loopback-only, hence unreachable from the distros: the upstream must listen on the WSL-facing address itself). Status is read-only; changes go through an elevated helper. |
| Diagnostics | Read-only state summary (port owners, chain processes — or the external-upstream egress line, WSL bind, counters, marker, CONNECT roundtrip, guards) in a window of its own; the log list/tail/rotation live in the CLI, the window has an "Open logs" button. |
| Localization | UI string dictionaries with on-the-fly switching (en/ru; `ui.language` in the config); the selector shows a drawn flag mark and the short code (en/ru), with the config's `auto` displayed as the language it resolves to. |

## License

[Unlicense](LICENSE) — public domain: use, modify and distribute without any restrictions, in open
and closed projects alike, no attribution required.