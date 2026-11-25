# Security Model

## Context

Two concerns meet here: keeping Claude/Codex traffic exclusively on the proxy chain (the
direct-access control), and keeping the repository and the privileged surface safe (no
secrets, minimal elevation, no injection). The application defends the first with layered
guards and the proxy's own refusals, and the second with a narrow runas helper and strict
repository rules.

## Direct-Access Enforcement

Layers, from policy to packet (each is independently observable in the guard status):

| Layer | Mechanism | Covers |
|---|---|---|
| Managed policy | HKLM `SOFTWARE\Policies\Claude\egressProxyUrl` + `disableWslSessions=0` | Claude Desktop's own egress |
| hosts block | managed block between config markers in the hosts file | finite known names |
| NRPT | one rule for all subtree apexes (`guard.nrptRuleName`) | wildcard DNS trees |
| Firewall | outbound Block rules by subnet, grouped (`guard.firewallGroup`) | direct-by-IP paths |
| Settings sync | proxy env + privacy keys in `settings.json`; proxy env in Codex `.env`; `respect_system_proxy = false` and the privacy switches (`[analytics]`/`[feedback]` off, update check off) in `config.toml` (the env is the transport) | the CLI cores inside both apps |
| WSL layer | startup script + profile.d snippet per distro | the CLI cores inside WSL |
| WinINET | system proxy on/off with save/restore | other Windows apps (opt-in) |

A ban is **on** only when all three local layers are present and current with the config
(mismatch = `Partial`, never silently "on"); after every apply/remove the guard runs live
probes (DNS, direct, proxied) — see [Guard](../domains/guard/README.md).

Profiles ([Profiles](../domains/configuration/profiles.md)) do not multiply this surface: the
hosts block, NRPT rule and firewall group stay one shared ban with one set of identifiers and
the same lists in every profile; the HKLM policy and the WinINET switch are single-valued, so
`guard.managePolicy`/`guard.manageSystemProxy` give each exactly one owning profile (the other
reads it and says "not managed by this profile"). The settings files and the two WSL halves are
split by app, so each file has one writer.

## Proxy Refusal Rules (defense in depth)

The forward proxy refuses before any dial, answering `403 Forbidden`
([Connect proxy](../domains/forward-proxy/connect-proxy.md)):

- target host `localhost` / `*.localhost`;
- IP literals that are loopback, unspecified (`0.0.0.0`/`::`, whole `0.0.0.0/8`), broadcast
  `255.255.255.255`, IPv4 link-local `169.254.0.0/16`, IPv6 `fe80::/10`;
- in the shapes where the proxy resolves names itself: any resolved address that falls in the
  above (DNS-rebinding defense) — mixed answers dial only the allowed addresses.

A CONNECT that is not an absolute-form `CONNECT` request is answered `400`; plain HTTP is
never proxied. The refusal is defense-in-depth, not the primary guard: the socks5 mode's
contract is that the trusted upstream resolves names.

## Elevation Model

- The application runs per-user and unelevated; elevation is a separate launch of the same
  binary via `runas` (the only UAC prompt in the system), executing one marked helper command
  (`--elevated-*`).
- The helper validates everything it takes on trust
  ([Elevated helper](../domains/elevated-helper.md)): result file only under the pinned
  `%ProgramData%\PrivateNetRouting\elevated` shape, config must be an absolute local `.json`,
  the statedir must be an inert absolute local path, only the three asking-user profile
  variables pass through `--env`. System binaries (PowerShell, hosts file, the standard
  per-machine Tailscale install) are pinned regardless of what the user-writable config names.
- The privileged surface is exactly: hosts file block, NRPT rule, firewall group, HKLM policy
  values, DNS cache flush, and (chain helper) start/stop of the sidecar daemon.

## Secrets and Identifiers

- The user config (`%APPDATA%\PrivateNetRouting\appsettings.json`) may hold the socks5 upstream
  RFC 1929 credentials; nothing else secret belongs in a file. Any other secret uses Windows
  Credential Manager / DPAPI.
- The repository carries no environment-specific values (addresses, node names, user names,
  bypass domains, keys, tokens): placeholders only (`config/*.example.json`, RFC 5737/3849
  documentation ranges where a literal is needed). Machine facts live in the local,
  git-ignored `.secret.agents.md` and in the user config.
- Logs may record host names of proxied targets and state changes; they never carry
  credentials (the socks5 user is shown in status lines, the password never).

## Invariants

- No drivers, no WFP callouts, no DLL injection, no LSP — all routing is proxy and policy
  ([ADR-001](../decisions/001-routing-without-drivers.md)).
- A user-writable config value never decides what runs elevated: the elevated helper executes
  only pinned system paths and warns when the config disagrees.
- The elevated helper writes only its own result file and its own log under the pinned
  directory; a result file outside the expected shape is refused.
- The forward proxy has no client authentication and says so in the log when `listen` is not a
  loopback address.
- Bypass lists and `NO_PROXY` always contain the loopback/private base entries from code; the
  config contributes zone names only.
- The repository stays anonymous: before every commit the diff is reviewed for addresses,
  names, and secrets (AGENTS.md rule #2 is the operative policy).

## Anti-Patterns

- **Creating a parallel ban rule with new identifiers** instead of maintaining the configured
  one — the status and removal logic key off `guard.*` identifiers.
- **Trusting a `--config`/`--env`-supplied path in the elevated helper without validation** —
  the helper runs as administrator against user-writable input.
- **Writing real endpoints into an example file, a spec, or a log sample that lands in the
  repo** — placeholders and documentation ranges only.
- **"Improving" the guard with a driver or injection approach** — see
  [ADR-001](../decisions/001-routing-without-drivers.md).
- **Reporting a ban as on while one layer lags the config** — staleness is `Partial` and
  re-offers the apply.
