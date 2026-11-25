# Configuration Store

## Role

`AppConfigStore` — load/create the user config, write UI changes surgically so comments and
layout survive (`JsonTextPatch`), and reload into a running composition with an honest
"restart required" note.

## Key Files

- `src/PrivateNetRouting.Core/Configuration/AppConfigStore.cs` — `LoadOrCreate`, `Update`/`UpdateAsync`, `ReloadInto`, `GetDefaultConfigPath`
- `src/PrivateNetRouting.Core/Configuration/JsonTextPatch.cs` — the byte-level surgical editor
- `src/PrivateNetRouting.Core/SystemOps/AtomicFile.cs` — one-step replace (temp + `File.Replace`/`Move`, symlink resolution)

## Behavior

**Load** (`LoadOrCreate`): read with shared access and sharing-violation retries; a missing
file is created from the coded defaults **before** expansion (the file keeps `%VAR%` as
written); the in-memory copy has exactly the documented path keys expanded (`logging.directory`,
`powerShellPath`, `forwardProxy.activityMarkerPath`, `chain.tailscale.stateDir`, `claude.aliasPath`,
`claude.settingsPath`, `claude.update.cacheDir`, `codex.settingsPath`, `codex.envPath`,
`browser.*` paths, `guard.hostsFile`). An explicit override map wins over the process
environment (the elevated helper passes the asking user's profile that way).

**Update** (`Update(mutate)`): read-modify-write serialized per process (`WriteGate`); the
mutated model is diffed against the read one and only changed values are spliced via
`JsonTextPatch` — every other byte (comments, formatting, key order, unknown keys, trailing
commas) survives; a no-op mutation does not touch the file. Keys match case-insensitively; of
duplicates the last one is edited (the one the loader keeps). A missing key is inserted into
its nearest existing parent object. On patch failure or read-back mismatch the raw tree is
rewritten (comments lost) with a log message. `UpdateAsync` queues writes in ask-order through
a task chain; a failed write does not cancel later ones. A symlinked config is written through
to its final target (the link stays a link); the replace is atomic and preserves the file's
ACL/attributes and BOM.

**Reload** (`ReloadInto(target)`): copies the fresh values into the running composition and
returns whether a restart is required — `true` when the `forwardProxy` or `chain` sections
differ (they are deliberately NOT replaced: the running proxy/chain stay one snapshot) or when
a composition-captured value differs: the whole `Logging` section, `PowerShellPath`,
`Claude.Enabled`, `Claude.AliasPath`, `Claude.Update.PackageName`, `Codex.Enabled`,
`Codex.PackageName`, `Codex.ProcessName`, and the WSL distro **names**. The watchdog and the
`claude`/`codex` sections (incl. nested `Update`) are copied in place so composition-time
references keep working; `Wsl`/`Browser`/`Guard`/`Ui`/top-level scalars are replaced wholesale.

## Error Handling

- A failed save leaves the previous file intact (atomic replace).
- A sharing violation on read retries 5× (50 ms steps) before failing.
- A patch failure degrades to a whole-file rewrite with a log line — never a half-edit.
- The create race of two starting processes: the loser re-reads the winner's file (there is no
  cross-process update lock beyond that).

## Invariants

- Exactly one writer at a time per process; a UI write never sees another's half state.
- Every content change goes through the diff + patch path first; comments and layout survive
  ordinary UI writes.
- The restart note is derived from what actually differs, not from "the file changed".
- The config path is the selected profile's: `%APPDATA%\PrivateNetRouting\appsettings.json` for
  the default profile, `%APPDATA%\PrivateNetRouting\profiles\<name>.json` for `--profile <name>`
  (the helper receives it as `--config`). A named profile is never created implicitly by a load —
  only `config init` creates it.
- Every writer of a profile's config (GUI settings, reload, browser profile persist, helper)
  uses that profile's path; two profiles write two different files.

## Related Specs

- [Configuration](README.md) — the model and its invariants
- [Host <-> Core](../../contracts/host-core.md) — `ReloadConfig`/`ConfigNeedsRestart` surfaced to the UI
