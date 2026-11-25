# Running Two Profiles Side by Side

A profile is a named config file plus its own process tree. Use it when two applications need
two different chains at the same time — for example, a desktop app through a tailscale sidecar
and another through an external SOCKS5 upstream — instead of switching one config back and
forth.

## The recipe

1. Create the second profile (a named profile is never created implicitly by a command):

   ```powershell
   pnr --profile example config init
   ```

   The file is `%APPDATA%\PrivateNetRouting\profiles\example.json`; the default profile stays
   `%APPDATA%\PrivateNetRouting\appsettings.json`. Edit the new file.

2. Give the profile its own resources. At least:

   | Key | Why |
   |---|---|
   | `forwardProxy.listen` | two listeners on one port: the second profile would route its app through the first chain |
   | `logging.directory` | the chain operation lock and the watchdog spawn lock live next to the logs; separate dirs keep the profiles independent |
   | `forwardProxy.activityMarkerPath` | each idle stopper must see only its own proxy's activity |

   In the tailscale mode a second tailscale profile also needs its own
   `chain.tailscale.stateDir`, `socket`, `socksListen` and `httpListen`.

3. Split the apps and the WSL halves: `claude.enabled`/`codex.enabled` decide whose settings
   files are synced; `wsl.syncClaude`/`wsl.syncCodex` decide whose WSL files are written. A
   two-profile setup enables one app and one WSL half per profile, so no file has two writers.

4. Name the owner of the machine-wide values: the Claude policy
   (`HKLM\SOFTWARE\Policies\Claude`) and the WinINET switch are single-valued. Keep
   `guard.managePolicy`/`guard.manageSystemProxy` true in the owning profile and set them false
   in the other; it then reports "not managed by this profile" and refuses the actions. The
   local ban (hosts/NRPT/firewall) stays shared: one set of identifiers, the same lists in both
   profiles; either profile may toggle it for the machine.

5. Start the two windows and raise the chains:

   ```powershell
   PrivateNetRouting.App.exe                              # default profile
   PrivateNetRouting.App.exe --profile example            # the second profile
   pnr --profile example chain up
   pnr --profile example check                            # CONNECT roundtrip through its proxy
   ```

   The windows differ by the title suffix (`profile.name`, else the `--profile` argument) and
   the tray tooltip; each has its own logs, locks, idle stopper and chain.

## Everyday commands

```powershell
pnr --profile example chain status|up|down
pnr --profile example guard status
pnr --profile example logs list
pnr --profile example codex launch          # or: launch, browser, update
```

`pnr` without `--profile` always means the default profile. `pnr --profile example config path`
prints the profile's file; `config init` creates it.

## Shortcuts

Create a second desktop shortcut whose target is the app exe with `--profile example` after the
path (Inno Setup creates only the default one). The application then gives it the profile's icon
by itself: a profile that enables one app wears that app's brand icon on the taskbar, in the tray
and on its shortcuts, so two profiles are told apart at a glance. Only shortcuts that start this
exe with this profile are touched, and a shortcut whose icon you picked by hand keeps it; the
icons live in `%APPDATA%\PrivateNetRouting\icons`. The installer's upgrade check sees any profile's
window through the profile-agnostic `AnyInstance` marker; the upgrade itself stops all profiles'
background workers, so raise the chains again afterwards.

## Limits

- `HKLM\SOFTWARE\Policies\Claude` and the WinINET switch remain single-valued: exactly one
  profile owns each.
- The ban is shared; disabling it in one profile disables it for the machine.
- The desktop apps themselves are single-instance. Two profiles work because each owns a
  different app (`claude.enabled` / `codex.enabled`), not because the same app can run twice.
- A second profile with the default forward-proxy port is refused when the port belongs to
  another profile's worker; the error names the profile and asks for its own
  `forwardProxy.listen`.
