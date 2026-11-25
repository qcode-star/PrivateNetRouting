# WSL Layer

## Role

The optional in-distro half of the guard: a boot-layer runner wired into `/etc/wsl.conf`, a
startup script that refreshes the proxy address at every distro start, a Codex proxy-env
snippet, the managed-proxy block of the distro's Codex `config.toml`, and the Claude settings/env
sync into every configured distro.

## Key Files

- `src/PrivateNetRouting.Core/Guard/WslConfigGuard.cs` — `WslConfigGuard`, `NormalizeLf`, `DistroPayload`, boot-layer planning, per-distro sync/status

## Behavior

**Layer evaluation** (`wsl.mode`): `off` -> off; both `syncClaude` and `syncCodex` off -> off
("nothing to sync"); no configured distros -> off; `on` -> on; `auto` -> on iff WSL answers and
at least one configured distro is installed.

**Boot wiring**: the startup script must live in a dedicated directory
(`…/<parent>/startup/<script>`, absolute, >= 4 segments) — otherwise sync refuses. The
`[boot] command` of `/etc/wsl.conf` is pointed at a marked runner
(`BootRunnerPath` = the startup dir's parent + `/startup.sh`). Planning
(`PlanBootLayer`) recognizes four cases:

| Command state | Runner state | Action |
|---|---|---|
| references the runner path | marked ours | `AlreadyOurs` (the runner **file** is still compared and refreshed if stale) |
| references the runner path | foreign file at the path | `TakeoverRunner` (replace the file, keep the command — wrapping would recurse) |
| empty | — | `InstallRunner` |
| something else | — | `AdoptForeignCommand` (preserved as `00-user-boot-command.sh`, runs first) |

`SetBootCommand` edits only the `[boot] command` line of `wsl.conf`; everything else survives
byte for byte. The runner executes every executable script of the startup dir in name order
(skipping `*.pnr-bak-*`/`*.bak-*`), logs to `/var/log/wsl-boot.log`, and continues past a
failing script unless `FAIL_FAST=true`. The already-done marker is the `PNR-BOOT-RUNNER` line
inside the runner.

**Startup script** (root, at every distro start): refreshes `HTTPS_PROXY`/`HTTP_PROXY` in the
distro user's `~/.claude/settings.json` to `http://<host>:<port>` — host = the baked external
address (`PNR_PROXY_HOST`) or the WSL NAT default-route gateway (recomputed each start;
5×2 s retries); `NO_PROXY` is deliberately untouched. A python3 heredoc does the JSON edit
(only the two env keys; unparsable/non-object files are refused and left untouched; pid-suffixed
temp + atomic replace; mode/owner preserved). It also creates `~/.claude` owned by the user and
runs the root-owned-`~/.claude` handback (only the directory, only when owned by uid 0, never
through a symlink, never for `root`).

**Codex snippet** (`wsl.proxyEnvPath`, under `/etc/profile.d/`): exports
`HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`/`NO_PROXY`/`NODE_USE_ENV_PROXY` **only** to Codex shells
(marker: `CODEX_HOME` in the environment, or `BASH_EXECUTION_STRING` matching
`*codex*|*Codex*`); ordinary shells, apt, pip and git stay direct. The host is recomputed every
shell start (fixed address or `ip route show default`); the port is baked as
`PNR_CODEX_PROXY_PORT` (read back by the status). It also rewrites `$CODEX_HOME/.env` to the
gateway form atomically when the file holds the Windows loopback form (the launcher restores it
before every launch).

**Codex config.toml** (the distro user, `~/.codex/config.toml`): the same
`[features.network_proxy]` block the Windows guard maintains (`enabled = true`,
`allow_upstream_proxy = true`, `enable_socks5 = false`, `[features.network_proxy.domains]`
`"*" = "allow"`), written surgically with the shared `CodexSettingsGuard` editor and the
`pnr-bak` backup helper. It turns on the Linux core's managed proxy for sandboxed commands and
lets that proxy use the env the snippet exported as its upstream. The permission profile still
decides whether the sandbox has network. A file with the `features` inline table or dotted
`features.network_proxy.*` keys is refused (the distro fails with the reason, nothing is
rewritten); a missing file is created with the block alone.

**Per-distro sync/status**: a distro reads `On` only when every enabled check matches —
script port/host == expected, `settings.json` proxy env fully in sync, Codex env file with the
expected port/host/NO_PROXY, the Codex `config.toml` network-proxy block present and current,
and `keysOk` (settings `NO_PROXY`, script `USER_NAME` == the configured user, boot wiring
`AlreadyOurs`, zero privacy/attribution drift). An unknown gateway -> `Partial`. Sync skips
not-installed/not-running distros with a reason and isolates per-distro failures.

## Error Handling

- The sync refuses a non-dedicated startup script path (nothing is written anywhere).
- A missing `PNR=ok` completion marker in any script output is a failure (wsl.exe exit codes
  hide bash failures).
- A root-owned `~/.claude` handback failure is fatal in the sync root step, log-only in the
  boot script.
- A script failure inside the distro leaves the user's files untouched ("left untouched — fix
  it by hand").
- A distro `config.toml` the shared editor cannot extend (an inline `features` table or dotted
  `features.network_proxy.*` keys) fails that distro's sync with the reason; the file is not
  rewritten.

## Invariants

- Every WSL-bound text is LF-normalized at the boundary (`NormalizeLf`) — a CRLF checkout can
  never break a distro script.
- Temp files inside the distro carry a pid suffix (a concurrent sync and boot pass cannot tear
  each other's writes).
- The boot pass always ensures the runner file itself, even in the `AlreadyOurs` case.
- Ordinary distro shells never see the proxy env (only Codex's do).

## Related Specs

- [Guard](README.md) — how the layer votes in the status
- [Settings sync](settings-sync.md) — the maintained shapes written into the distro
- [Watchdog WSL stoppers](../watchdog/wsl-app-stoppers.md) — the same distro list, stopping instead of syncing
- [Configuration](../configuration/README.md) — `wsl.*` keys
