# Browser Profile

## Purpose

A dedicated browser profile that always egresses through the chain: created once through the
dialog ("?" help menu), then launched by the "Browser" button or the desktop shortcut. Two
phases — creation (write the profile files, record the config) and launch (probe the egress,
start) — and two families: Firefox (managed `user.js`) and Chromium (command-line flags plus a
WebRTC policy seed).

## Key Files

- `src/PrivateNetRouting.Core/Launcher/BrowserProfile.cs` — `BrowserProfileFiles`, `BrowserProfileFolderKind`, `BrowserProfileCommandLine`, `BrowserExecutableLocator`, `BrowserProfileService`, `BrowserEgress`
- `src/PrivateNetRouting.Core/Launcher/BrowserLauncher.cs` — `BrowserLauncher`, `BrowserLauncherOptions`, `BrowserLaunchOutcome`, `BrowserBypassList`
- `src/PrivateNetRouting.App/BrowserProfileWindow.xaml` + `src/PrivateNetRouting.App/BrowserProfileWindow.xaml.cs` — the creation dialog

## Core Types

```csharp
public enum BrowserProfileFolderKind { Fresh, Ours, Foreign, BrowserUserData }

public static class BrowserEgress {
    // "forward-proxy" (default) -> AppProxyEndpoint; "chain" -> the chain's final egress
    public static string ResolveEndpoint(AppConfig config);   // scheme://address
}

public sealed record BrowserProfileRequest(BrowserKind Kind, string ProfileDir,
    string ExecutablePath, bool CreateDesktopShortcut, string ShortcutPath = "");

public sealed record BrowserLauncherOptions(bool CheckOnly = false, bool Private = false,
    string? Url = null, Func<CancellationToken, Task<bool>>? ConfirmDeadEgress = null,
    ChainRaisedLinks? RaisedLinks = null);
```

## Flow

```
creation (once):  dialog -> ClassifyFolder -> write family files -> optional shortcut
                  -> browser.* written back to the config
launch (every):   classify folder (refuse foreign/browser-data) -> TCP probe of the egress
                  -> dead egress? (ConfirmDeadEgress contract, see launch.md)
                  -> start executable with the family's command line
                  -> Firefox: user.js refreshed first; Chromium: nothing written
```

Egress modes (`browser.egress`):

| Mode | Endpoint | Name resolution | Needs our process? |
|---|---|---|---|
| `forward-proxy` (default) | `AppProxyEndpoint` | proxy side, past the ban | yes in the default shape (the proxy runs); no in a skipped shape |
| `chain` | `chain.tailscale.socksListen` / `chain.socks5.upstream` / `chain.http.upstream` | tailscale: system resolver (ban names do NOT open — the launch warns); socks5/http: the upstream | no (the chain only) |

## Invariants

- Only the profile folder is touched — never the browser's system settings or the everyday
  profile.
- A browser's real data folder is refused outright at creation and refused at launch
  (classification is shared: `BrowserProfileFiles.ClassifyFolder`).
- An instance already running with our profile is not duplicated (command-line match on the
  profile dir as a whole argument).
- `--private`/`--url` come only from CLI flags — the launch opens no URL of its own.
- The first click without a profile offers to create one (warning, not a silent creation).

## Configuration

| Key | Default | Notes |
|---|---|---|
| `browser.egress` | `forward-proxy` | `forward-proxy` \| `chain`; unknown -> `forward-proxy` |
| `browser.type` | `""` | `firefox` \| `chrome`; empty = not configured |
| `browser.executablePath` | `""` | empty -> standard-place lookup at launch |
| `browser.profileDir` / `shortcutPath` | `""` | written by the creation dialog |
| `browser.probeTimeoutSeconds` | 5 | TCP egress probe |

## Extension Points

- A new browser family: implement its file set in `BrowserProfileFiles`, its command line in
  `BrowserProfileCommandLine`, add a `BrowserKind` value and locator candidates; keep the
  classification and probe contracts unchanged.
- A new egress shape: teach `BrowserEgress.ResolveEndpoint` and the dead-egress ownership rule
  (`ChainOwnsEgress`) about it.

## Related Specs

- [Profile files](profile-files.md) — creation, classification, per-family files
- [Launch](launch.md) — probe, dead-egress question, start
- [Forward proxy](../forward-proxy/README.md) — the default egress
- [Chain](../chain/README.md) — the `chain` egress
