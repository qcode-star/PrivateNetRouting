# Contract: Application <-> Claude/Codex Files

## Boundary Rule

The application maintains only the keys and files listed here inside the two desktop apps'
own configuration; every other byte of those files belongs to the app and must survive.

## Interfaces

| Maintained shape | File | Maintained by | Consumed by |
|---|---|---|---|
| Claude Code proxy env + privacy + attribution | `~/.claude/settings.json` | `ClaudeSettingsGuard` (sync), WSL startup script (the two proxy keys) | the Claude Code engine |
| Managed egress policy | HKLM `SOFTWARE\Policies\Claude` (`egressProxyUrl`, `disableWslSessions`) | `EgressPolicyStore` | Claude Desktop |
| Codex proxy-mode flag + privacy switches + managed network proxy | `~/.codex/config.toml` (`[features] respect_system_proxy = false`, `[analytics] enabled = false`, `[feedback] enabled = false`, top-level `check_for_update_on_startup = false`, `[features.network_proxy]` + `.domains`) | `CodexSettingsGuard` | the Codex CLI core |
| Codex proxy env | `~/.codex/.env` (`HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`, `NODE_USE_ENV_PROXY`) | `CodexEnvFile` (sync + launcher refresh + WSL snippet) | the Codex CLI core |
| Codex flag + privacy switches + managed network proxy in the distro | `<distro>:~/.codex/config.toml` (`[features] respect_system_proxy = false`, `[analytics] enabled = false`, `[feedback] enabled = false`, top-level `check_for_update_on_startup = false`, `[features.network_proxy]` + `.domains`) | `WslConfigGuard` (the shared `CodexSettingsGuard` editor) | the Codex CLI core inside WSL |
| Claude launch flags | command line: `--proxy-server`, `--proxy-bypass-list` | `ClaudeLauncher` | Claude Desktop (Chromium) |
| Codex launch flags + env | package-context command line + PowerShell env | `CodexLauncher` | the Codex desktop app + its children |
| Browser profile files | profile dir: `user.js` / `Default/Preferences` + `.pnr-profile` | `BrowserProfileFiles` | Firefox / Chromium |

## Initialization

Written on demand: `pnr guard settings sync` / the GUI guards card / the WSL sync; refreshed by
the Codex launcher before every launch; rewritten by the browser launch (Firefox `user.js`
only). Nothing is written for an app disabled in the config (`claude.enabled`/`codex.enabled`).
With two profiles each file has one writer — the Claude profile owns Claude's files, the Codex
profile owns Codex's — so one file never carries two proxy endpoints
([Profiles](../domains/configuration/profiles.md)).

## Data Flow Across Boundary

Into the files: the AppProxyEndpoint URL, the `NO_PROXY`/bypass list (base entries fixed in
code + `bypassDomains` zones in two spellings + `directTlds` `*.tld` where the consumer knows
TLD wildcards), the privacy/attribution constants, the `respect_system_proxy = false` flag, the
Codex privacy switches (`analytics`/`feedback` off, update check off) and the
managed-network-proxy block. The block's `domains` entry allows every host; the permission
profile, never this contract, decides whether the sandbox has network. Out of the files: nothing
is read except for the drift comparison (status) and the `.env`-gateway-form acceptance.

## Error Propagation

- A malformed maintained file is never rewritten (`InvalidDataException`/`Unknown` status).
- A non-object `env`/root in `settings.json` is a hard refusal for the whole sync of that file.
- Backups (`.pnr-bak-<n>`, three slots, mtime-ordered, content-deduped) precede every
  content-changing rewrite of the app-owned files.

## Breaking Change Checklist

If you change X, you MUST also update Y:

- Add/rename/remove a maintained key -> `ClaudeSettingsShape`/`CodexEnvFile` (or the config.toml
  editor), the sync status comparison (status = "every maintained value matches"), the WSL
  payload where it mirrors the shape, and the tests (`GuardTests`).
- Change the Codex proxy-mode flag or the network-proxy block -> `CodexSettingsGuard.FindValue`/
  `Apply`/`ConfigUpToDate`/`NetworkProxyUpToDate`/`ApplyNetworkProxy`, the WSL copy
  (`WslConfigGuard`: the read probe, the status comparison and the sync pass), the status wording
  (`GuardService.BuildItems`), `docs/codex-proxy.md` (the flag and sandbox paragraphs) and
  `GuardTests`.
- Change the Codex privacy switches -> `CodexSettingsGuard.PrivacyUpToDate`/`ApplyPrivacy`/
  `ConfigUpToDate`, the WSL copy (the shared `ConfigUpToDate` and `ApplyPrivacy` call), the status
  wording (`GuardService.BuildItems`, the GUI strings), `docs/codex-proxy.md` and `GuardTests`.
- Change the bypass/NO_PROXY construction -> every consumer spelling (Claude flags, Codex env,
  system proxy bypass, browser prefs/command line), `ClaudeEnvValues`, and `LauncherTests`/
  `BrowserProfileTests`/`GuardTests`.
- Change the policy value names or shape -> `EgressPolicyStore`, the launcher's policy warning,
  and `GuardTests`.
- Change the launch flag spellings -> `ClaudeLauncher`/`CodexLauncher`, the running-copy parser
  (`ParseProxyUrl`), and the docs (`docs/claude-proxy.md`, `docs/codex-proxy.md`).
