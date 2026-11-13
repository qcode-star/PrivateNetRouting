# Browser Launch

## Role

The launch half of the browser profile: probe the chosen egress, decide what to do when it is
dead, and start the browser.

## Key Files

- `src/PrivateNetRouting.Core/Launcher/BrowserLauncher.cs` — `BrowserLauncher`, `BrowserLauncherOptions`, `BrowserLaunchOutcome`, `IsOurBrowserRunning`

## Behavior

```
launch(options):
  1. classify the profile folder (BrowserUserData/Foreign -> refuse before the probe)
  2. TCP probe of the egress (browser.probeTimeoutSeconds)
  3. dead egress:
       ConfirmDeadEgress == null  -> IOException
       answers false              -> Cancelled
       answers true + chain owns the egress -> raise the chain (rail draws it), re-probe
       answers true + external endpoint    -> launch over the dead upstream anyway
  4. Firefox: refresh user.js + marker first; Chromium: write nothing
  5. start the executable with the family's command line
```

"Chain owns the egress" (`ChainOwnsEgress`): the `chain` egress in the tailscale mode, or the
forward-proxy egress while it is ours to run (not a skipped shape). Over an external endpoint
(the socks5/http upstream with `egress=chain`, or the skipped-shape egress) there is nothing
of ours to raise — the question is only "start anyway?"; a refusal is `Cancelled`.

Outcomes: `Launched`, `CheckOnly` (probe only), `Cancelled`. The result carries
`EgressResponded` and the elapsed time. "Already running" (our profile dir as a whole argument
in another process's command line) short-circuits to a note, not a duplicate instance.

## Error Handling

- A dead egress with no one to ask (`ConfirmDeadEgress == null`) -> `IOException`.
- A malformed egress address -> Warn + "not reachable" (never a crash).
- Caller cancellation propagates through the probe untouched.
- The tailscale `chain` egress logs a warning at launch: the sidecar resolves names with the
  system resolver, so ban names do not open through it.

## Invariants

- The probe is a plain TCP connect — no traffic is generated beyond the handshake.
- A refused launch (classification) happens before any probe or process start.
- The dead-egress chain raise is drawn in the chain card exactly like a manual bring-up.
- The browser never starts twice for one profile.

## Related Specs

- [Browser profile](README.md) — phases and egress modes
- [Profile files](profile-files.md) — what the command line and files contain
- [Chain orchestration](../chain/orchestration.md) — the raise behind a confirmed dead egress
