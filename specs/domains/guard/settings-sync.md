# Settings Sync

## Role

The maintained shape of the two desktop apps' own configuration files: Claude Code's
`settings.json` (proxy env + privacy + attribution), Codex's `config.toml` (the sysproxy flag
and the managed network-proxy block) and `CODEX_HOME/.env` (the proxy env the embedded CLI core
loads) — surgical edits with backups, on Windows and (via the WSL layer) inside distros.

## Key Files

- `src/PrivateNetRouting.Core/Guard/ClaudeSettingsShape.cs` — the maintained key manifest
- `src/PrivateNetRouting.Core/Guard/ClaudeSettingsGuard.cs` — `ClaudeSettingsGuard`, `ClaudeEnvValues`, backup machinery
- `src/PrivateNetRouting.Core/Guard/CodexSettingsGuard.cs` — the `respect_system_proxy` flag editor
- `src/PrivateNetRouting.Core/Guard/CodexEnvFile.cs` — the `.env` merge (shared with the Codex launcher)

## Behavior

**Claude `settings.json` maintained keys** (`ClaudeSettingsShape`):

| Group | Keys | Value |
|---|---|---|
| Proxy env (9 spellings) | `HTTPS_PROXY`, `HTTP_PROXY`, `https_proxy`, `http_proxy`, `ALL_PROXY`, `all_proxy`, `CLAUDE_CODE_HTTP_PROXY`, `CLAUDE_CODE_HTTPS_PROXY`, `grpc_proxy` | the AppProxyEndpoint URL |
| No-proxy (2 cases) | `NO_PROXY`, `no_proxy` | `BuildNoProxy(bypassDomains)` |
| Resolution switch | `CLAUDE_CODE_PROXY_RESOLVES_HOSTS` | `"1"` |
| Privacy (10 keys) | `DISABLE_TELEMETRY`, `DISABLE_ERROR_REPORTING`, `DISABLE_BUG_COMMAND`, `DISABLE_FEEDBACK_COMMAND`, `CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, `DISABLE_AUTOUPDATER`, `DISABLE_GROWTHBOOK`, `DO_NOT_TRACK`, `CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL` | `"1"` |
| Attribution | `includeCoAuthoredBy` (root, `false`), `attribution = {commit:"", pr:""}` | empty trailers |

`NO_PROXY` = `localhost`, `127.0.0.1`, `.local` + each `bypassDomains` zone in two forms
(`zone`, `.zone`). Every other key of the file survives; the file is re-serialised (indented
JSON — comments of the original are not kept). A non-object `env`/root is a hard refusal:
`InvalidDataException`, file untouched.

**Codex `config.toml`**, two maintained tables:

| Table | Keys | Value |
|---|---|---|
| `[features]` | `respect_system_proxy` | `false` |
| `[features.network_proxy]` | `enabled`, `allow_upstream_proxy`, `enable_socks5` | `true`, `true`, `false` |
| `[features.network_proxy.domains]` | `"*"` | `"allow"` |

The flag is maintained **off**: with `true` Codex resolves the system proxy first and, when the
system proxy is disabled, pins the route to direct (`no_proxy()`), so the proxy env of `.env` is
ignored — login token exchange (`auth.openai.com/oauth/token`) and the desktop account/workspace
requests then go direct and fail. With `false` the core keeps reqwest's default proxy behavior,
which reads `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` from `.env`; that is the transport.

The network-proxy block turns on Codex's own managed proxy for the commands a permission profile
lets out to the network, and `allow_upstream_proxy` makes that proxy use the proxy env of
`.env` for its upstream — the command traffic then leaves through the AppProxyEndpoint instead of
a direct route. The domain entry allows every host: the layer is a chaining layer, not a filter.
The permission profile still decides whether the sandbox has network at all; the guard never
edits `[permissions]` or `default_permissions`. The editor recognizes the flag inside
`[features]`, the top-level dotted form and an inline `features = { … }` table; it rewrites the
line in place (keeping key spelling, spacing and inline comment), inserts after `[features]` or a
dotted sibling, or appends the section. Multi-line values are tracked so a header inside a string
is not mistaken for a section; newline style is kept. The network block is edited by section
form only: `features = { … }` and top-level `features.network_proxy.*` keys are refused with
`InvalidDataException` rather than rewritten into invalid TOML. An already-correct text is
returned unchanged (idempotence).

**Codex `.env`** (`CodexEnvFile`): maintained keys in order `HTTP_PROXY`, `HTTPS_PROXY`,
`ALL_PROXY`, `NO_PROXY`, `NODE_USE_ENV_PROXY` (proxies = the AppProxyEndpoint URL;
`NO_PROXY` = `BuildNoProxy`; `NODE_USE_ENV_PROXY` = `1` — Node's built-in HTTP clients ignore the
proxy env without it, and Codex's browser/computer-use helpers are Node programs). Any spelling of
a maintained key (lowercase, `export ` prefix) is rewritten to canonical uppercase `KEY=value`;
foreign lines survive byte for byte; the file always ends with a newline. `UpToDate` accepts the
Windows form **or** the WSL-gateway resting form (same scheme `http`, same port, host a private
IPv4) for the proxy values; `NO_PROXY` and `NODE_USE_ENV_PROXY` must match exactly. The launcher
refreshes the Windows form back before every launch.

**Backup machinery** (shared): before a rewrite the file is copied to `<name>.pnr-bak-<n>` —
three numbered slots; a free slot first, else the oldest-mtime slot is reused. A state already
present among the backups is not copied again — that copy is mtime-touched instead (so mtime
order mirrors preserved states). After every sync `PruneBackups` keeps the 3 newest and deletes
legacy `<name>.bak-<timestamp>` copies.

**App gating**: `claude.enabled=false` / `codex.enabled=false` — the app's files are never
written or created, its status reads `Off` with "not synced", and its half has no vote.

## Error Handling

- A malformed maintained file is never rewritten (status `Unknown`, `Sync` refuses).
- A `features` inline table or dotted `features.network_proxy.*` keys leave the network block
  unedited: `Sync` refuses with `InvalidDataException` instead of producing invalid TOML (the
  flag editor still supports those shapes for `respect_system_proxy`).
- Per-app failures are isolated (`SettingsSyncResult.ClaudeError`/`CodexError`) — one app's
  failure never stops the other's sync.
- A write only happens when the content actually differs (idempotence); backups are still
  pruned.

## Invariants

- Only the maintained keys are touched; every foreign line/byte of `config.toml` and `.env`
  survives.
- A backup exists before every content-changing rewrite (and dedupes by content).
- Both apps' proxy values always name the AppProxyEndpoint — never a hardcoded port.
- The guard never edits `[permissions]` or `default_permissions`: whether the Codex sandbox has
  network is the app's decision; the network block only chains traffic that the profile allows.
- `respect_system_proxy` is always `false`: the `.env` proxy env is the transport. `true` switches
  the core to the system proxy and, with the system proxy off, disables env routing altogether.
- The sync is the same shape on Windows and in the WSL layer (shared manifest): the WSL layer
  writes the Codex `.env` and the same `[features.network_proxy]` block into the distro's
  `~/.codex/config.toml` (see [WSL layer](wsl-layer.md)).

## Related Specs

- [Guard](README.md) — status semantics (`On` = zero drift)
- [WSL layer](wsl-layer.md) — the in-distro copy of this shape
- [Proxied apps](../../contracts/proxied-apps.md) — the contract these files belong to
- [Codex launcher](../launchers/codex.md) — the same `.env` refreshed at launch
