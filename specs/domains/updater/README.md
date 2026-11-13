# Updater

## Purpose

Two manual update pipelines behind one contract: Claude Desktop updates as an MSIX package
through the chain (check → download → Publisher verification → install), and the Codex desktop
app updates through the Microsoft Store. Both close the app first, both report a version that
did not move as `NoChange` (a failure, not a success), and the Claude download keeps the
activity marker fresh so idle auto-stop never kills the chain under it.

## Key Files

- `src/PrivateNetRouting.Core/Updater/ClaudeUpdater.cs` — the MSIX orchestrator (detail: [Claude MSIX update](claude-msix.md))
- `src/PrivateNetRouting.Core/Updater/CodexUpdater.cs` — the Store orchestrator (detail: [Codex Store update](codex-store.md))
- `src/PrivateNetRouting.Core/Updater/ClaudeProcessSource.cs` — running-Claude handles for the close step (shared)
- `src/PrivateNetRouting.Core/Updater/PowerShellAppxPackageSource.cs` — `Get-AppxPackage`/`Add-AppxPackage` wrapper

## Core Types

```csharp
public enum UpdatePhase { Check, Chain, Download, Verify, Install, Done }
public enum UpdateOutcome { UpToDate, Downloaded, Installed, NoChange }
public sealed record UpdateRunResult(UpdateOutcome Outcome, Version? Installed,
    Version? InstalledAfter, string? PackageFile, string? PackageUrl, bool Reinstall = false);
```

## Flow

Shared skeleton:

```
check offered vs installed
  -> up to date?          -> UpToDate (nothing closed, nothing kept)
  -> (Claude) find URL -> chain -> download -> verify -> install
  -> (Codex) Store check -> close -> Store update -> poll
  -> re-read installed version:
       reached the offered version -> Installed
       unchanged                   -> NoChange  (CLI exits non-zero)
```

Shared close semantics (before an install): graceful `CloseMainWindow`, wait
`closeWaitSeconds` (1 s polls), tree kill, settle `killSettleSeconds`. `keepRunning` skips the
close entirely (Claude: no `-ForceApplicationShutdown`, an in-use install fails instead).

## Invariants

- The update check never raises the chain and never asks UAC (Claude: the feed is consulted
  only over an already-live proxy).
- An observed version that did not reach the package's is `NoChange`, not a success.
- A downloaded package of the installed version is up to date unless `--force` asked for a
  reinstall — Claude is not closed, nothing is installed.
- The download keeps the activity marker fresh (once before, then on every progress tick).
- A cached package that fails verification, or that the installer refuses, is removed unless
  `--keep-file`; a user-pointed file (`--install-from`) is never deleted.
- The app being updated is closed by the shared close contract
  ([Running app](../launchers/running-app.md)) before any install.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `claude.update.packageName` / `packageHost` | Claude / downloads host | identity + URL gate |
| `claude.update.updateFeedUrl` | Claude's own update feed | empty = do not ask (journal stays) |
| `claude.update.cacheDir` | `%LOCALAPPDATA%\PrivateNetRouting\update-cache` | chunks + packages |
| `claude.update.chunkBytes` / `parallelChunks` / `chunkRetries` / `retryDelaySeconds` | 8 MiB / 6 / 3 / 5 | download tuning |
| `claude.update.chunkTimeoutSeconds` / `headTimeoutSeconds` | 1800 / 30 | per-chunk / HEAD bound |
| `codex.update.storeProductId` | public Store id | catalog key |
| `codex.update.updateVersionUrl` | OpenAI's version manifest | empty = no offered version |
| `codex.update.updateTimeoutSeconds` | 1800 | hard limit of one Store update |
| `closeWaitSeconds` / `killSettleSeconds` | 25 / 3 | the shared close |

## Extension Points

- A new package source: implement `IAppxPackageSource` (installed packages + install) or
  `IUpdateUrlFinder` (URL discovery) and wire it in `ProxyRuntime.FromConfig`.
- A new app updater: follow the skeleton — check without side effects, one chain phase, a
  resumable or store-backed transfer, identity verification, `NoChange` honesty.

## Related Specs

- [Claude MSIX update](claude-msix.md) — URL finders, downloader, verification, flags
- [Codex Store update](codex-store.md) — the Store flow and version manifest
- [Chain](../chain/README.md) — the `Chain` phase of the Claude run
- [Activity marker](../activity-marker.md) — kept fresh during downloads
