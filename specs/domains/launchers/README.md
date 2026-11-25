# Launchers

## Purpose

Start the two desktop apps (Claude, Codex) through the proxy chain: verify the chain live,
bring it up idempotently on failure, build the proxy flags/bypass lists, warn about a policy
gap or an already running copy without flags, and handle the restart-consent flow. Also owns
the scan/report/close machinery for an already running copy, and the owner's manual stop of
either app on Windows and in WSL ([App stop](app-stop.md)).

## Key Files

- `src/PrivateNetRouting.Core/Launcher/ClaudeLauncher.cs` — `ClaudeLauncher`, `ClaudeBypassList`, `LauncherOptions`, `LaunchOutcome`, `LaunchResult`
- `src/PrivateNetRouting.Core/Launcher/CodexLauncher.cs` — `CodexLauncher`, `CodexLaunchOutcome` (package-context launch; detail in [Codex](codex.md))
- `src/PrivateNetRouting.Core/Launcher/RunningApp.cs` — `RunningApp`, `RunningAppState`, `IRunningAppCloser`, `WindowsRunningAppCloser` (detail in [Running app](running-app.md))
- `src/PrivateNetRouting.Core/Launcher/AppStopper.cs` — `AppStopper`, `AppStopTarget`, `AppStopResult` (detail in [App stop](app-stop.md))

## Core Types

```csharp
public sealed record LauncherOptions(bool CheckOnly, bool RestartRunning = false,
                                     ChainRaisedLinks? RaisedLinks = null);

public enum LaunchOutcome { Launched, CheckOnly, RestartedExisting, Reported }

public sealed record LaunchResult(LaunchOutcome Outcome, bool ChainRaised,
                                  TimeSpan Elapsed, bool RestartedExisting = false);

public enum AppStopTarget { Claude, Codex }

public sealed record AppStopResult(AppStopTarget Target, bool Disabled,
                                   RunningAppCloseResult Windows, WslHostStopResult? Wsl,
                                   TimeSpan Elapsed);

public static class ClaudeBypassList {
    // fixed base + each bypassDomains zone in two forms ("zone", "*.zone"), joined with ";"
    public static string Build(IEnumerable<string> domains);
}
```

## Flow

```
LaunchAsync(options, progress, ct)
  1. live CONNECT health check
       -> healthy: continue
       -> dead:    Chain.EnsureRunningAsync(progress, options.RaisedLinks)   [ChainRaised=true]
  2. CheckOnly? -> LaunchOutcome.CheckOnly
  3. WarnIfPolicyMissing()   (HKLM egressProxyUrl read: match=Info, else Warn; never blocks)
  4. running copy?
       RestartRunning && WithoutProxy -> graceful close + tree-kill survivors -> RestartedExisting
       otherwise (incl. Proxied)      -> Report only (Info/Warn to the log)
  5. start with --proxy-server=<AppProxyEndpoint.Url> --proxy-bypass-list=<bypass>
```

The bypass base entries are fixed in code: `<local>`, `localhost`, `127.0.0.1`, `[::1]`, the
private ranges (`10/8`, `172.16/12`, `192.168/16`), `169.254/16`, `100.64/10` (CGNAT/tailnet),
`*.local`. The config contributes zone names only, each in both spellings.

The Codex launcher runs the same skeleton (steps 1-4 with "Codex" naming) and then a
package-context start — see [Codex](codex.md).

Manual stop (the window's "Stop Claude" / "Stop Codex", no launch involved):

```
AppStopper.StopAsync(target)
  Windows: the target launcher's CloseRunningAsync (any copy, a proxied one included)
  WSL:     one WslAppHostStopper pass after the close, exactly that app's flags,
           running configured distros only (a closing app may restart its WSL host)
  the chain is untouched
```

## Invariants

- The owner-consent question ("restart the running copy?") is the caller's (GUI dialog / CLI
  prompt); Core only exposes the state and performs the close on consent
  (`NoteRestartDeclined()` logs a refusal).
- A launch/restart never closes a copy **with** our proxy flags (`Proxied`); the owner's manual
  stop closes any copy ([ADR-008](../../decisions/008-manual-app-stop.md)).
- The health check never raises the chain by itself; the bring-up happens exactly once, inside
  `LaunchAsync`, and is reported in `LaunchResult.ChainRaised`.
- The launch address always comes from `AppProxyEndpoint` — never from `forwardProxy.listen`.
- A launch cancelled mid-bring-up rolls back only the links it raised itself
  (`ChainRaisedLinks`), not a pre-existing chain.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `claude.enabled` / `codex.enabled` | `true` | false: the app's commands refuse with a note |
| `claude.aliasPath` | WindowsApps execution alias | env vars expanded |
| `claude.processName` / `codex.processName` | `Claude` / `ChatGPT` | running-copy detection |
| `bypassDomains` | `[]` | zones; two spellings per zone everywhere |
| `restartGraceSeconds` | 15 | WM_CLOSE wait before the forced tree kill: the launch restart and the manual stop |
| `closeWaitSeconds` / `killSettleSeconds` | 25 / 3 | shared with the updaters' app close |

## Extension Points

- A new proxied app: add its section to `AppConfig`, a launcher following the same skeleton
  (health -> chain -> warnings -> running-copy -> start with flags), and its update dot wiring
  in the hosts. Keep the consent policy in the hosts.

## Related Specs

- [Codex](codex.md) — the package-context launch and env refresh
- [Running app](running-app.md) — the scan/close contract
- [App stop](app-stop.md) — the owner's manual stop (Windows + WSL)
- [Chain](../chain/README.md) — the bring-up behind step 1
- [Guard](../guard/settings-sync.md) — the settings/env layer the flags mirror
- [Proxied apps](../../contracts/proxied-apps.md) — the maintained shapes the flags belong to
