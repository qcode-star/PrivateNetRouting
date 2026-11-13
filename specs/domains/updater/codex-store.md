# Codex Store Update

## Role

The Codex desktop app update through the Microsoft Store (WinRT `AppInstallManager`): search
the catalog by product id, hand the update to the Store, poll the install item — no downloads
of our own, no UAC, no chain.

## Key Files

- `src/PrivateNetRouting.Core/Updater/CodexUpdater.cs` — `CodexUpdater`, `CodexUpdateCheck`
- `src/PrivateNetRouting.Core/Updater/StoreUpdateSource.cs` — `StoreUpdateSource`, `IStoreUpdateSource`, `IStoreInstallHandle`, `StoreInstallStatus`
- `src/PrivateNetRouting.Core/Updater/CodexVersionManifest.cs` — `CodexVersionManifest` (OpenAI's offered-version manifest)

## Behavior

**Check** (`CheckAsync`): `Get-AppxPackage` by `codex.packageName` (absent -> `IOException`) ->
`StoreUpdateSource.HasUpdateAsync(storeProductId)` (a non-null catalog item = update; the
Store answer carries no version) -> if an update exists, `CodexVersionManifest` (GET
`codex.update.updateVersionUrl` through the live proxy; a manifest that names a different
storeProductId is ignored; any failure = "no version", never hides the update). Result:
`CodexUpdateCheck(Installed, UpdateAvailable, StoreUnavailable, OfferedVersion)`. A Store
failure is `StoreUnavailable=true` (Warn, not an error). No chain, no UAC.

**Update** (`UpdateAsync`): read the installed package (needs `PackageFamilyName`, else
`IOException`) -> close Codex (shared close contract; reported as the `Check` phase
deliberately) -> `UpdateAppByPackageFamilyNameAsync` (a null item -> `IOException` "the Store
queued no update…" — Codex is already closed) -> `onHandle` callback (the window's "Stop"
button cancels the Store item) -> poll `GetStatus()` every 1 s until `Completed`/`Error`/
`Canceled` under `updateTimeoutSeconds`. Completed -> re-read: a differing version is
`Installed`; unchanged is **NoChange** (CLI non-zero — Codex has no force-reinstall). Caller
cancellation cancels the Store item then rethrows; timeout cancels then throws.

`StoreInstallStatus` maps the WinRT install state: `Waiting`, `Downloading`, `Installing`
(Installing/RestoringData), `Completed`, `Canceled`, `Error`.

## Error Handling

- Store/catalog failure -> `StoreUnavailable`, never a crashed check.
- No queued install item -> `IOException` naming the reason (the check may be stale).
- Timeout / user cancel -> the Store item is cancelled first, then the error/cancellation
  surfaces.
- A completed install with no new registered version -> `NoChange` (the same honesty rule as
  the MSIX pipeline).

## Invariants

- The check never closes Codex and never touches the Store beyond the catalog query.
- A manifest failure never hides an offered update (it only withholds the version number).
- The update runs under one hard deadline (`updateTimeoutSeconds`).
- The Store item is always cancelled on the way out of a failed/cancelled run.

## Related Specs

- [Updater](README.md) — the shared skeleton and close semantics
- [Codex launcher](../launchers/codex.md) — the same app identity the update keys on
- [Proxied apps](../../contracts/proxied-apps.md) — where the manifest URL lives
