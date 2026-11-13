# Browser Profile Files

## Role

The creation half of the browser profile: folder classification, the per-family proxy files,
the shortcut, and the config write-back.

## Key Files

- `src/PrivateNetRouting.Core/Launcher/BrowserProfile.cs` — `BrowserProfileFiles`, `BrowserProfileCommandLine`, `BrowserExecutableLocator`, `BrowserProfileService`

## Behavior

**Classification** (`ClassifyFolder(directory, configuredDirectory, everydayRoots)`) in order:

| Order | Condition | Verdict |
|---|---|---|
| 1 | directory does not exist | `Fresh` |
| 2 | `.pnr-profile` marker present | `Ours` |
| 3 | managed `user.js` (first line `// Managed by PrivateNetRouting:`) | `Ours` |
| 4 | equals the configured dir AND NOT inside `EverydayBrowserDataRoots()` | `Ours` |
| 5 | browser-data signs (`Local State`, `Default\Preferences`, `Preferences`, `Secure Preferences`, a `Default`/`Profile N` under a dir with `Local State`, `prefs.js`, `places.sqlite`) | `BrowserUserData` |
| 6 | non-empty, nothing of the above | `Foreign` (advisory only) |
| 7 | empty | `Fresh` |

`BrowserUserData` is refused outright (never written into, never launched);
`Foreign` is an advisory the dialog surfaces; `Ours` re-creates silently.

**Firefox files**: `user.js` — all protocols through the chosen egress (the forward-proxy
egress writes http/ssl prefs of the application's CONNECT proxy; the chain egress writes SOCKS5
with `socks_remote_dns=true`, or http/ssl when the chain egress is an HTTP proxy),
`share_proxy_settings=true`, `no_proxies_on` from the bypass zones (both spellings + `*.tld`
from `directTlds`), `failover_direct=false`, `trr.mode=5`, restore-last-session
(`startup.page=3`), startup offers off (welcome/upgrade/what's-new/default-browser/telemetry),
WebRTC off (`media.peerconnection.enabled`/`media.navigator.enabled`), built-in VPN off. A
fresh profile also gets a one-time `prefs.js` seed accepting the Terms-of-Use page — only when
no `prefs.js` exists yet, never in `user.js` (which would pin it forever).

**Chromium files**: the WebRTC policy merged into `Default/Preferences`
(`webrtc.ip_handling_policy = "disable_non_proxied_udp"`); existing keys survive. Everything
else rides the command line (the family's command-line proxy switch still works; the WebRTC
switch is gone from current builds).

**Command lines** (`BrowserProfileCommandLine.Build`):
- Firefox: `-profile <dir> --new-instance [-private-window] [--url …]` (proxy in `user.js`).
- Chromium: `--proxy-server=<url> --proxy-bypass-list=<list> --user-data-dir=<dir>
  --no-first-run --no-default-browser-check --restore-last-session [--incognito] [--url …]`.
- Quoting follows `CommandLineToArgvW` rules (backslash-before-quote doubling).

**Service** (`BrowserProfileService.Create`): writes the family files + `.pnr-profile` marker,
optional desktop shortcut (`IShortcutFactory`; default
`Desktop\PrivateNetRouting <DisplayName>.lnk`, missing parent folders created), and persists
`browser.type/executablePath/profileDir/shortcutPath` through `AppConfigStore.Update`.
Idempotent; a profile elsewhere is never deleted. `SuggestProfileDir`:
`%LOCALAPPDATA%\PrivateNetRouting\browser\{firefox-profile|chrome-profile}`.

**Executable lookup** (`BrowserExecutableLocator`): Firefox in `%ProgramFiles%` /
`%ProgramFiles(x86)%`; Chromium family: Chrome (ProgramFiles(x86)/ProgramFiles/LOCALAPPDATA),
then Edge as last resort. Any file can be pointed at by hand.

## Error Handling

- A `BrowserUserData` folder: refused at creation and at launch, nothing written.
- An empty/missing executable path: `Create` refuses.
- An unreadable Chromium `Preferences` file: the merge is skipped with the keys untouched
  (never a half-written policy).

## Invariants

- The profile folder holds no browser data outside the files named here plus the marker.
- Firefox's `user.js` is the single source of its proxy truth and is rewritten on every
  application launch (only in a folder the classification accepts).
- The Chromium policy merge preserves every existing key of `Default/Preferences`.
- A shortcut path is exactly where the user pointed it — the desktop default is only a
  suggestion.

## Related Specs

- [Browser profile](README.md) — phases and egress modes
- [Launch](launch.md) — the other half
- [Proxied apps](../../contracts/proxied-apps.md) — bypass-list spellings shared with the apps
