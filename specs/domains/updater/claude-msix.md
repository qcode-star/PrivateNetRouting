# Claude MSIX Update

## Role

The Claude Desktop update orchestrator: version check, package URL discovery, chunked download
through the proxy chain, Publisher verification, and the MSIX install.

## Key Files

- `src/PrivateNetRouting.Core/Updater/ClaudeUpdater.cs` — `ClaudeUpdater`, `UpdateOptions`, `UpdateCheckResult`, `PackageVerificationException`
- `src/PrivateNetRouting.Core/Updater/UpdateUrlFinder.cs` — `UpdateFeedUrlFinder`, `PowerShellJournalUrlFinder`, `PackageUrl`, `IUpdateUrlFinder`
- `src/PrivateNetRouting.Core/Updater/FragmentDownloader.cs` — `FragmentDownloader`, `ByteSize`
- `src/PrivateNetRouting.Core/Updater/MsixPackage.cs` — `MsixIdentity`, `TryReadIdentity`
- `src/PrivateNetRouting.Core/Updater/PackageVersion.cs` — four-component version compare

## Behavior

**Phases** (`UpdatePhase`): `Check -> Chain -> Download -> Verify -> Install -> Done` (`Verify`
is not reported as progress).

**URL sources, in precedence** (`FindPackageUrlAsync`):
1. `--url` (manual) — overrides everything.
2. `UpdateFeedUrlFinder` — GET `claude.update.updateFeedUrl` with `device_id=pnr-update-check`
   and version/os query (over the live proxy only — the check never raises the chain); parse
   `{currentRelease, releases:[{version, updateTo:{url}}]}`; any failure -> Warn + fall through.
3. `PowerShellJournalUrlFinder` — the Windows AppXDeploymentServer operational journal
   (`Get-WinEvent`, `journalMaxEvents`), the first event matching the configured package host;
   the URL is extracted and gated (`PackageUrl.IsTrustedPackageUrl`: absolute https `.msix`, no
   userinfo, exact host match).

**Download** (`FragmentDownloader`): `chunkBytes`-sized ranges (`part{n:0000}` under
`<target>.parts`), up to `parallelChunks` workers, per-chunk retry (`chunkRetries` with
`retryDelaySeconds`), per-chunk timeout, HEAD for the total size. Resume manifest
`resume.manifest` binds parts to exactly (`url`, `length`, `chunkBytes`); a mismatch discards
the whole `.parts` directory. A second concurrent download of the same target is refused
(`<target>.lock`). Success concatenates, verifies the length, deletes `.parts`; an already
complete target short-circuits. Cancellation keeps the parts (resumable). The activity marker
is touched before and throughout.

**Verification** (`VerifyPackageAsync`, run at Verify and again before install):
`MsixIdentity` from `AppxManifest.xml` — Name and Publisher must equal the installed package's
(case-insensitive), architecture must match the OS (or `neutral`), the package version must not
be older than installed. Failures throw `PackageVerificationException`; a cached failing
package is deleted unless `--keep-file`.

**Install**: re-verify; the installed-version check (`IsInstalledVersion`) short-circuits to
`AlreadyInstalled` unless `--force`; close Claude (unless `--keep-running`);
`Add-AppxPackage -ForceApplicationShutdown` (suppressed by `--keep-running`); an installer
refusal removes a cached file (unless `--keep-file`); re-read the installed version —
reaching the package's version is `Installed`, otherwise `NoChange`. `--force` accepts only
the reinstall of the version it asked for: a `NoChange` of a newer package is still a failure.

**Flags** (`UpdateOptions`): `--check` (check only), `--download-only`, `--force` (reinstall),
`--keep-file` (never delete the package), `--keep-running` (no close, no forced shutdown),
`--url <url>`, `--install-from <path>` (install a given file, no download). A flag the chosen
mode would ignore is an error (`UpdateOptions.ConflictWith`).

## Error Handling

- URL discovery failures are warnings — the check answers "no address known", never "an update
  exists".
- Download failures keep the parts for resume; a permanently failed chunk throws
  `IOException("incomplete fragments …")` after the other workers finish.
- `NoChange` is a failed run (CLI non-zero) except a `--force` reinstall of the installed
  version.
- A chain failure during the `Chain` phase is a `ChainException` — the run stops there.

## Invariants

- The check phase runs before any chain bring-up (no UAC for "nothing to install").
- Every download URL passes the https + host + `.msix` gate, manual URLs included; redirects
  are not followed (`AllowAutoRedirect=false`).
- An older package is refused before Claude is closed.
- The resume manifest binds parts to one exact (url, length, chunkBytes) triple.
- The cache-removal rule never touches a file outside `cacheDir`.

## Related Specs

- [Updater](README.md) — the shared skeleton and close semantics
- [Activity marker](../activity-marker.md) — refreshed during the download
- [Chain](../chain/README.md) — the `Chain` phase
- [Protocols](../../contracts/protocols.md) — the feed/journal expectations
