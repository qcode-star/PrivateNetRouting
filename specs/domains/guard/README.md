# Guard

## Purpose

Direct-access control for Claude and Codex: the managed egress policy, a three-layer local ban
(hosts + NRPT + firewall), the maintained settings/env shape of both apps (Windows and WSL),
and the WinINET system proxy. The guard status is read-only truth about all of it; every apply
or removal ends in live probes, never "it should work".

## Key Files

- `src/PrivateNetRouting.Core/Guard/GuardService.cs` — orchestration: status, ban/policy, settings/wsl/sysproxy sync, live probes
- `src/PrivateNetRouting.Core/Guard/GuardModel.cs` — `GuardKind`, `GuardState`, per-layer status records
- `src/PrivateNetRouting.Core/Guard/GuardAggregate.cs` — folds layer states into the collapsed-card summary
- `src/PrivateNetRouting.Core/Guard/GuardElevatedCommands.cs` — the elevated commands (see [Elevated helper](../elevated-helper.md))
- plus one file per layer: [ban-layers](ban-layers.md), [settings-sync](settings-sync.md), [wsl-layer](wsl-layer.md), [system-proxy](system-proxy.md)

## Core Types

```csharp
public enum GuardState { On, Partial, Off, Unknown }

public sealed record GuardSnapshot(/* six layer statuses */);
// LocalBanStatus, EgressPolicyStatus, ClaudeSettingsStatus, CodexSettingsStatus,
// SystemProxyStatus, WslConfigStatus — each carries GuardState + a reason/detail

public sealed record BanLiveChecks(bool? DnsBlocked, string DnsDetail,
    bool? DirectBlocked, string DirectDetail, bool? ProxiedAlive, string ProxiedDetail);
```

## Flow

```
GetStatusAsync -> six independent layer reads (a throwing layer is Unknown + reason,
                  one broken layer never takes the snapshot down)
operations:
  BanAsync(on/off)     -> one elevation (--elevated-guard-ban-on/off) -> LiveChecksAsync
  PolicyAsync(on/off)  -> one elevation (--elevated-guard-policy-on/off)
  AllAsync(on/off)     -> one elevation (--elevated-guard-all-on/off) = ban + policy -> LiveChecks
  EnableAllAsync()     -> AllAsync(true) then settings sync then WSL sync (best-effort,
                          each failure isolated) then LiveChecks
  SyncSettingsAsync()  -> Claude settings.json + Codex config.toml/.env   (no elevation)
  SyncWslAsync()       -> the distro layer                              (no elevation)
  SetSystemProxyAsync(on/off)                                          (no elevation)
```

Layer verdicts:

| Layer | `On` requires | `Partial` | `Off` |
|---|---|---|---|
| Local ban | hosts block present+current AND NRPT > 0 AND firewall > 0, all current with config | any layer present but missing/stale | nothing present |
| Egress policy | `egressProxyUrl` = expected AND `disableWslSessions=0` | set but wrong | no url |
| Claude settings | full proxy env in sync AND zero privacy/attribution drift | any drift | file absent; app disabled in config |
| Codex settings | `respect_system_proxy=false`, the network_proxy block and `.env` up to date | any of the three missing/wrong | both files absent; app disabled in config |
| WSL | every enabled check of every active distro matches | mixed / unknown gateway | layer off / nothing active |
| SystemProxy | enabled AND ours AND no PAC/AutoDetect override | enabled otherwise | disabled |

**Live probes** (`LiveChecksAsync`, after every ban change): DNS resolution of each
`guard.banProbes` name (`HostNotFound`/`NoData` = blocked), a direct HTTPS GET with proxies
off (connection error/timeout = blocked; TLS errors and responses = open), and the proxied
CONNECT roundtrip through the chain (down = "skipped", not a verdict). Combined: any open =
open; all blocked = blocked; otherwise null ("not checked").

## Invariants

- A ban is `On` only when all three local layers are present **and** current with the config —
  a lagging layer makes it `Partial`, and the card re-offers the apply.
- A malformed maintained file (unparseable `settings.json`, unterminated hosts block) is never
  rewritten: status `Unknown`/refusal instead.
- An app disabled in the config (`claude.enabled`/`codex.enabled=false`) is skipped entirely:
  no file of it is written or created, and its half has no vote in the status.
- The elevated helper pins system locations (PowerShell, hosts file) regardless of config.
- `GuardAggregate` treats SystemProxy as non-voting; a layer with no verdict has no vote;
  Unknown dominates; all voting layers on = all-on.

## Configuration

`guard.*` (rule identifiers — the app maintains one ban with these identifiers, never a
parallel one):

| Key | Default | Notes |
|---|---|---|
| `hostsFile` | `%SystemRoot%\System32\drivers\etc\hosts` | env vars expanded |
| `hostsBeginMarker` / `hostsEndMarker` / `hostsComment` | managed-block markers | exact-line matches |
| `nrptRuleName` / `nrptComment` | rule identifiers | DisplayName of the one NRPT rule |
| `firewallGroup` | rule identifier | Group of the firewall Block rules |
| `banProbes` | 2 probe names | inside ban trees, outside hosts lists |

The name lists themselves (`claude.banHosts/banTrees/firewallSubnets`, same for `codex`) live
in the app sections; the union is exposed as `BanHosts`/`BanTrees`/`FirewallSubnets`.

## Extension Points

- A new guard layer: give it a status record in `GuardModel`, a read in `GetStatusAsync`
  (isolate failures as `Unknown`), and — if it applies changes — a command behind the
  elevated helper plus live probes.
- A new maintained key: add it to `ClaudeSettingsShape`/`CodexEnvFile` and cover it in both
  the sync and the status comparison (status = "every maintained value matches").

## Related Specs

- [Ban layers](ban-layers.md) — hosts/NRPT/firewall and the probes
- [Settings sync](settings-sync.md) — settings.json / config.toml / .env shapes
- [WSL layer](wsl-layer.md) — the distro layer
- [System proxy](system-proxy.md) — the WinINET switch
- [Security model](../../architecture/security-model.md) — how the layers compose
