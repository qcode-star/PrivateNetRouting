# CLI

## Purpose

The headless `pnr` console: every chain/proxy/guard/launch/update/log verb of the system, with
exit codes scripts can rely on, plus the hidden worker and elevated-helper bodies.

## Key Files

- `src/PrivateNetRouting.Cli/Program.cs` — `Main` dispatch, all verb handlers, exit codes, help text

## Core Types

The CLI has no domain model of its own — it composes `ProxyRuntime` and maps outcomes to exit
codes. The types it owns are the parsing/sync helpers in `Program.cs`:

```csharp
internal sealed class SyncProgress<T> : IProgress<T>;   // marshals Core progress to the console
// exit codes: 0 = success / normal no-op, 1 = failure or NoChange,
//             2 = check unhealthy / helper without --result-file, 130 = Ctrl+C
```

## Flow

```
Main(args):
  global:  --profile <name>    (anywhere; default — the default config); stripped before dispatch
  hidden:  --proxy-worker      -> ProxyWorker.RunAsync        (Ctrl+C-linked token)
           --watchdog-worker   -> WatchdogWorker.RunAsync
           --elevated-*        -> ElevatedChainCommands / GuardElevatedCommands
                                 (--result-file required; --config, --env NAME=value)
  visible: version | help | proxy | chain | check | status | logs
           | launch | browser | codex | update | watchdog | guard | config
  every verb: LoadConfig() (profile path; a named profile must exist)
              -> await using ProxyRuntime.FromConfig(config, profile: <profile>)
```

`--profile` selects the config file and every per-profile resource (logs, locks, ports, the
stopper identity); `pnr config init --profile <name>` creates a missing named profile, `config
path` prints its path ([Profiles](configuration/profiles.md)).

| Verb | What it does | Exit codes |
|---|---|---|
| `proxy [--detached]` | in-process forward proxy (or spawn the worker); skipped shapes print a notice | 0 (notice) / 1 (port held) |
| `chain status\|up\|down` | state print / bring-up / stop | 0 / 1 |
| `check` | CONNECT roundtrip | 0 alive / 2 dead |
| `status` | diagnostics summary (short notice on a stopped chain) | 0 |
| `logs list\|show <name> [--tail N]\|rotate [<name>...]` | log store | 0 / 1 (rotate with a locked file) |
| `launch [--check-only] [--no-splash] [--restart]` | Claude through the chain; `--restart` = yes to the consent question; redirected stdin = no | 0 / 1 |
| `browser [--check-only] [--private] [--yes] [--url <url>]` | probe + start the profile | 0 / 1 |
| `codex launch [--check-only] [--restart]` / `codex update [--check]` | Codex launch / Store update | 0 / 1 |
| `update [--check] [--download-only] [--force] [--keep-file] [--keep-running] [--url <url>] [--install-from <path>]` | Claude MSIX update | 0 / 1 (`NoChange` = 1 except a `--force` reinstall) |
| `watchdog` | foreground idle stopper | 0 (incl. "beside a running stopper") / 1 (chain not fully up) |
| `guard status [--live] \| ban on\|off \| policy on\|off \| settings sync \| wsl sync \| sysproxy on\|off` | guard surface | 0 / 1 (ban not holding) |
| `config path\|init` | config path print / create | 0 |

**Exit-code conventions**: 0 = success or a normal no-op refusal (watchdog beside a stopper,
declined restart, browser already running); 1 = failure or `NoChange`; 2 = `check` unhealthy
and helper-without-result-file; 130 = cancelled by Ctrl+C.

**Host-side exit orchestration** (commands that leave a client behind: `chain up`, `launch`,
`codex launch`, `browser`, `update`, and their failure paths): await the watchdog auto-start
re-arm first, then hand the in-process proxy listener over to a detached worker
(`runtime.HandOverProxyAsync`), returning non-zero when the handover failed. `pnr` output is
English always (rule #1).

## Invariants

- A flag the chosen mode would ignore is an error, not a silent acceptance
  (`UpdateOptions.ConflictWith`).
- The hidden markers are exactly the ones both hosts recognize (`--proxy-worker`,
  `--watchdog-worker`, `--elevated-*`) — see [Host <-> Core](../contracts/host-core.md).
- A verb never mutates state in `--check-only` mode.
- The watchdog's "refused beside a running stopper" is exit 0 (a normal no-op), while "chain
  not fully up" is exit 1 (a real refusal).

## Configuration

No CLI-specific keys: every verb reads the one user config (`%APPDATA%\PrivateNetRouting\appsettings.json`)
and the per-module keys documented in the domain specs. Output is always UTF-8; output language is
always English.

## Extension Points

- A new verb: compose from `ProxyRuntime`, map outcomes to the exit-code conventions above,
  and keep prompts skippable behind a flag (the `--yes`/`--restart` pattern) for scripted use.

## Related Specs

- [Host <-> Core](../contracts/host-core.md) — the facade and exit contracts
- [Chain](chain/README.md), [Guard](guard/README.md), [Updater](updater/README.md) — the verbs' bodies
- [App shell](app-shell/README.md) — the GUI counterpart
