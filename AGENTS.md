# AGENTS.md

Private personal project: a WPF application on .NET 10 LTS (Windows Desktop) that brings under a
single window the functionality for maintaining Claude Desktop on this machine: a local proxy with
DoH resolution, a proxy chain supervisor, a launcher, idle auto-stop,
an MSIX updater, direct-access blocking, and diagnostics. Below are the rules that must not be
broken and the shape of the application; the detailed architecture, behavior and interfaces live
in `specs/` — start at `specs/INDEX.md`.

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
row, don't append a note. The same applies to `specs/` (see `specs/META.md` for their update rules).

## Architecture

A single WPF application (single instance, tray + windows) plus a headless CLI mode. Core modules
do not depend on the UI (testability); the UI is a thin wrapper. Everything is composed from the
user config in `ProxyRuntime.FromConfig`. Detailed behavior, flows, invariants and contracts live
in `specs/` — the table below is the map, not the spec.

Application modes (the owner's decision):

- **normal GUI** (`PrivateNetRouting.App` with no arguments): the status/control window; closing or
  minimizing hides it to the tray; the real exit is only from the tray menu — "Exit" keeps the chain
  (the in-process proxy is handed to a detached worker), "Exit with full chain stop" brings it down
  first — or the end of the Windows session; a second launch shows the hidden window;
- **shortcut GUI** (`--launch`, `--launch-browser`): the desktop shortcuts — the chain bring-up is
  drawn in the chain card, then Claude/browser starts with proxy settings;
- **console** (`pnr`, a separate exe): the headless commands.

Profiles (the owner's decision): one process tree per profile,
`--profile <name>` → `%APPDATA%\PrivateNetRouting\profiles\<name>.json` (no option — the default
`appsettings.json`). Each profile has its own chain, proxy port, logs, locks, watchdog, window
and tray; the GUI single-instance identity is per profile. The machine-wide guard values
(policy, WinINET) have one owning profile via `guard.managePolicy`/`guard.manageSystemProxy`;
the ban stays one shared block. See [profiles](specs/domains/configuration/profiles.md) and
[ADR-007](specs/decisions/007-profiles.md).

Ports per the owner's decision: **18081** (forward proxy, the address for
the client), **18082** (daemon HTTP), **18083** (daemon SOCKS5). Every module takes its addresses/ports/names from the
config; they change only by explicitly editing the user config. A second profile must use its
own forward-proxy port (and its own tailscale state/socket in the tailscale mode).

| Module | What it does | Spec | Implementation |
|---|---|---|---|
| ForwardProxy | local CONNECT proxy — the translator every client points at (CONNECT ↔ SOCKS5/DoH per chain mode, target refusals, listener placement and handover) | [forward-proxy](specs/domains/forward-proxy/README.md), [doh](specs/domains/doh-resolution.md) | `Core.Net.*`; `pnr proxy` |
| ChainSupervisor | idempotent chain bring-up/shutdown in three shapes (`chain.mode`: tailscale/socks5/http), cross-process operation lock, daemon elevation | [chain](specs/domains/chain/README.md) | `Core.Chain`; `pnr chain up\|down\|status`, `pnr check` |
| Launcher | Claude/Codex desktop launches through the chain (proxy flags, bypass lists, restart of a copy without flags), and the owner's manual stop of either app on Windows and in WSL | [launchers](specs/domains/launchers/README.md), [app-stop](specs/domains/launchers/app-stop.md) | `Core.Launcher`; `pnr launch`, `pnr codex launch` |
| Browser profile | created-once browser profile (Firefox/Chromium) and its launch through the chain | [browser](specs/domains/browser/README.md) | `Core.Launcher.BrowserProfile` + `BrowserLauncher`; `pnr browser` |
| Watchdog | idle auto-stop: app hosts in running WSL distros, then the chain; detached stopper worker | [watchdog](specs/domains/watchdog/README.md) | `Core.Watchdog`; `pnr watchdog` |
| Updater | manual MSIX update of Claude through the chain; Codex update through the Microsoft Store | [updater](specs/domains/updater/README.md) | `Core.Updater`; `pnr update`, `pnr codex update` |
| Guard | direct-access control: managed policy, three-layer local ban, settings/env sync (Windows + WSL), WinINET system proxy | [guard](specs/domains/guard/README.md) | `Core.Guard`; `pnr guard …` |
| App self-update | GitHub releases check, an offer to open the releases page (no auto-install) | [app-update](specs/domains/app-update.md) | `Core.AppUpdate` |
| Diagnostics | read-only state summary; log list/tail/rotation | [diagnostics](specs/domains/diagnostics.md) | `Core.Diagnostics`; `pnr status`, `pnr logs …` |

Architecture essentials (the specs hold the details):

- Threads: the WPF host never blocks the UI thread on Core async work (`Task.Run` where a
  synchronous wait is unavoidable) and Core start/stop paths use `ConfigureAwait(false)` — a
  blocking wait + a dispatcher continuation is a deadlock (regression test
  `StopCompletesUnderBlockedSynchronizationContext`). See [layers](specs/architecture/layers.md).
- Lifetimes: chain links outlive the application as detached processes; the forward proxy and the
  idle stopper can run in detached workers (`--proxy-worker`, `--watchdog-worker`); nothing is ever
  written to autostart (rule #4). See [ADR-006](specs/decisions/006-detached-processes-no-autostart.md).
- Elevation: only a separate runas helper of the same binary with validated, pinned arguments —
  never a restart of the app. See [elevated-helper](specs/domains/elevated-helper.md).
- Logs: one canonical file per log shared by all writers, size rotation, records in English. See
  [logging](specs/domains/logging.md).
- UI: everything visual comes from the `App.xaml` token dictionary (code goes through `Theme`); a
  state is always a word next to the colour, machine data is monospaced. See
  [app-shell](specs/domains/app-shell/README.md).

Releases and installation: `Directory.Build.props` holds the version and must match the release
tag (the workflow checks); a `v*` tag runs tests, then `packaging/make-release.ps1` (self-contained
publish of App+Cli, the portable zip, the Inno Setup installer) and attaches zip + setup to a
GitHub release. The installer is per-user (`%LOCALAPPDATA%\Programs\PrivateNetRouting`, no
elevation), terminates the background workers before touching the files (they have no window, so
the Restart Manager cannot close them), and writes no autostart entries (rule #4). The uninstaller
reverts nothing outside its folder: it asks first, listing the commands that turn the guards off
(`pnr guard ban|policy|sysproxy off`, `pnr chain down`).

## Repository structure

```
src/
  PrivateNetRouting.App/     # WPF host: windows, tray, single instance, localization,
                             # windowless worker/helper roles (HeadlessHost)
  PrivateNetRouting.Core/    # all logic (no UI): Configuration, Logging, Net/{Dns,Socks,Proxy},
                             # Chain, Launcher, Updater, Guard, Watchdog, Diagnostics, AppUpdate,
                             # SystemOps, Runtime; ProxyRuntime composes everything from AppConfig
  PrivateNetRouting.Cli/     # the headless `pnr` console
tests/
  PrivateNetRouting.Tests/   # xUnit against Core only (per-module test classes + Fakes.cs)
config/
  appsettings.example.json   # user config template with placeholders
packaging/
  setup.iss                  # Inno Setup: per-user install, no autostart (rule #4)
  make-release.ps1           # self-contained publish of App+Cli + zip + installer
.github/
  workflows/release.yml      # v* tag: tests -> make-release.ps1 -> GitHub release
specs/                       # the specification system — start at specs/INDEX.md
```

Per-file roles live in the "Key Files" sections of the specs. The test project references
`PrivateNetRouting.Core` only (fakes in `Fakes.cs` and beside the tests that need them), so the
WPF app and the CLI have no automated coverage: for changes in them, build and exercise the real
window/command, and keep logic in Core so it stays testable.

The user config is `%APPDATA%\PrivateNetRouting\appsettings.json` (created on first launch from the
default values) for the default profile, `%APPDATA%\PrivateNetRouting\profiles\<name>.json` for
`--profile <name>` (created explicitly by `pnr --profile <name> config init`); logs are in
`%LOCALAPPDATA%\PrivateNetRouting\logs\` (a second profile should use its own subdirectory).

## Specifications

Detailed system specs live in `specs/`. Before making structural changes, read the relevant spec:

- Start with `specs/INDEX.md` to find the right document for your task.
- `specs/META.md` defines spec formats and update rules.

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
  only. Rotation by size at start and during writes; manual rotation — from diagnostics (by the
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

- `specs/INDEX.md` — the specification system: task → spec navigation (architecture, domains,
  contracts, ADRs). Read the relevant spec before structural changes.
- `.secret.agents.md` (repository root, git-ignored, the deliberate exception to rule #1) — the
  machine-specific facts: real addresses, distro and user names, references to sources. Read it
  when a task needs real environment values; never copy its contents into a commit or another file.
- `config/appsettings.example.json` — the authoritative config schema with placeholders. A new
  setting goes here and into `AppConfig` before any code reads it (rule #3).
- `README.md` — the human-facing module overview.
- `docs/claude-proxy.md`, `docs/codex-proxy.md` — the sanitized hands-on guides: putting Claude
  Desktop/Claude Code and the Codex desktop app through the local forward proxy, on Windows and
  inside WSL (mechanics only, placeholders instead of environment values).
- `docs/profiles.md` — the sanitized two-profile recipe: a named config, its own ports/logs/
  marker, the app and WSL halves split between profiles, guard ownership.
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

If `dotnet test` dies at testhost start with `OpenProcess` access denied (error 5) only when the
binaries live under the repository folder (elevated runs and copies elsewhere work), look at the
folder's integrity label first: an agent sandbox run with workspace write may leave an inheritable
Low mandatory label on it (`icacls <repo>` shows the Low mandatory level), so every exe built there
starts at Low integrity and cannot open the Medium-integrity processes of the same user. No
antivirus log shows it. The fix is to put the label back to the default:
`icacls <repo> /setintegritylevel "(OI)(CI)M"`.
