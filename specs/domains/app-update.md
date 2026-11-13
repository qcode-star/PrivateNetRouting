# App Self-Update

## Purpose

Check the application's own GitHub releases against the running version and offer to open the
releases page when a newer one exists. Deliberately no auto-install — the installer is a manual
per-user step.

## Key Files

- `src/PrivateNetRouting.Core/AppUpdate/AppUpdateChecker.cs` — `AppUpdateChecker`, `AppUpdateCheck`, `IAppReleaseFeed`
- `src/PrivateNetRouting.Core/AppUpdate/GitHubAppReleaseFeed.cs` — anonymous `api.github.com` latest-release feed

## Core Types

```csharp
public sealed record AppUpdateCheck(Version Current, Version? Latest, string? ReleaseUrl) {
    public bool HasUpdate => Latest is not null && Latest > Current;
}
public interface IAppReleaseFeed {
    Task<(string Tag, string Url)?> GetLatestReleaseAsync(CancellationToken ct);
}
```

## Flow

```
CheckAsync -> feed.GetLatestReleaseAsync()      // GET api.github.com/repos/<repo>/releases/latest
           -> ParseTagVersion(tag)              // optional v-prefix, Version.TryParse
           -> AppUpdateCheck(current, latest, releaseUrl)
consumers:  GUI "?" menu  -> visible message box on failure, offer on update
            GUI startup   -> quiet log on failure (ui.checkAppUpdatesOnStart)
```

The request is anonymous (no auth header), fixed `User-Agent: PrivateNetRouting`, 10 s
timeout, a plain handler — no chain and no proxy wiring. A 404 (nothing published) is
"no update", not an error; other non-success statuses throw; a missing/malformed body field
is "no update".

## Error Handling

- Network failures propagate to the caller: the menu path shows them, the start check logs
  quietly and never interrupts startup.
- An unparsable release tag is "no update" (never an error).

## Invariants

- The check is read-only and side-effect free: no download, no install, no chain, no UAC.
- The current version comes from the running assembly's version (the release tag must match
  `Directory.Build.props` — enforced by the release workflow).
- The update offer opens the releases page in the browser; the application never installs
  itself.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `ui.checkAppUpdatesOnStart` | `true` | the one-time start check |

## Extension Points

- A different release source: implement `IAppReleaseFeed` and wire it in
  `ProxyRuntime.FromConfig` — the comparison and the offer stay as they are.

## Related Specs

- [App shell](app-shell/README.md) — the "?" menu and the start check
- [Updater](updater/README.md) — the desktop apps' updates (different concern)
