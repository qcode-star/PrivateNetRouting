# System Proxy

## Role

The WinINET system proxy on/off switch: point other Windows apps at the applications proxy
endpoint (opt-in), restoring the owner's own settings exactly when switched off. PAC scripts
and automatic detection are read and reported, never changed.

## Key Files

- `src/PrivateNetRouting.Core/Guard/SystemProxyStore.cs` — `ISystemProxyStore`, `WinInetSystemProxyStore`, `SystemProxyBypass`

## Behavior

Registry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` — values
`ProxyServer`, `ProxyOverride`, `ProxyEnable`; own-record `ProxyServer.pnr-ours`; saves
`ProxyServer.pnr-saved`, `ProxyOverride.pnr-saved`, `ProxyEnable.pnr-saved`.

**Enable** (`Enable(server, bypass)`): save the owner's `ProxyServer`/`ProxyOverride`/
enabled-state once (skipped when the current values are already ours or the save already holds
the current value), write the record **first** (a crash between writes never leaves our value
without a record), then the values, `ProxyEnable=1`, and a WinINET settings-change notification.

**Disable** (`Disable(ourServer?)`): "ours" = the recorded value matches, or the current server
equals our endpoint (legacy pre-record builds). Outcomes (`SystemProxyDisableOutcome`):

| Ours? | Save present? | Action |
|---|---|---|
| no | no | `NothingOurs` — values untouched |
| no | yes | stale save dropped (with the record); foreign switch untouched (`LeftForeign`) |
| yes | no | values deleted, `ProxyEnable=0` (`Cleared`) |
| yes | yes | owner's values + enabled-state restored; save + record consumed (`Restored`) |

**PAC/AutoDetect**: `AutoConfigURL` (non-empty = PAC) and the auto-detect bit in
`Connections\DefaultConnectionSettings` are read into `SystemProxyStatus.Overridden` — the
status is `Partial` when one overrides the manual proxy, `sysproxy on` warns, and neither is
ever modified.

**Bypass** (`SystemProxyBypass.Build`): fixed base entries (loopback, `<local>`, `*.local`,
private-range masks, `172.16–31.*`) + `*.tld` per `directTlds` + each `bypassDomains` zone in
both forms.

## Error Handling

- A registry read error -> `Unknown` status, no operation attempted.
- A repeated `off` never wipes a user's restored settings (recognized by the address this
  installation writes and by the saved state).

## Invariants

- The owner's own `ProxyServer`/`ProxyOverride`/enabled-state are restored exactly, once —
  the save is consumed by the restore.
- The record is written before the values (crash-safe ordering).
- A foreign manual proxy (not the address this installation writes) is never disabled or
  overwritten.
- PAC and auto-detection stay untouched in every operation.

## Related Specs

- [Guard](README.md) — where the status votes (SystemProxy is non-voting in the aggregate)
- [Configuration](../configuration/README.md) — the endpoint the switch writes
