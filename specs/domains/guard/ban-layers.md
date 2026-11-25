# Ban Layers

## Role

The three local layers of the direct-access ban — a managed hosts block, one NRPT rule for all
subtrees, a firewall outbound Block by subnet — plus the staleness comparison and the live
probes that verify a change on real traffic.

## Key Files

- `src/PrivateNetRouting.Core/Guard/HostsFileBlock.cs` — managed block in the hosts file
- `src/PrivateNetRouting.Core/Guard/BanSystemOps.cs` — `IBanSystemOps`, `PowerShellBanSystemOps` (NRPT + firewall)
- `src/PrivateNetRouting.Core/Guard/BanLayersMatch.cs` — applied-vs-configured comparison
- `src/PrivateNetRouting.Core/Guard/GuardService.cs` — `LiveChecksAsync`, `BanAsync`, `AllAsync`

## Behavior

**Hosts block** (`HostsFileBlock.Apply/Remove/Read`): begin marker line, optional comment,
`0.0.0.0 <domain>` lines (the union of `banHosts`), end marker. `Apply` is idempotent (an
equal block is a no-op); `Remove` no-ops when absent. An unterminated block (begin without
end) makes `Read` report "present, 0 entries, not current" and `Apply`/`Remove` **refuse**
("refusing to rewrite it"). The rewrite is encoding-preserving (UTF-8 BOM kept, Latin-1
fallback), CRLF, atomic (`File.Replace` from a temp file), retrying sharing violations; foreign
lines are untouched.

**NRPT** (`BuildNrptScript`): one rule for all subtrees — `-Namespace` = every `banTrees` apex
in both spellings (apex and `.<apex>`), `-NameServers 127.0.0.1`, `-DisplayName =
guard.nrptRuleName`, `-Comment = guard.nrptComment`; remove-then-add (idempotent), `DnsClient`
cmdlets.

**Firewall** (`BuildFirewallScript`): rules grouped by address family (a subnet containing `:`
is IPv6), each `New-NetFirewallRule -DisplayName "<nrptRuleName>: <family>" -Group
<guard.firewallGroup> -Direction Outbound -Action Block -Profile Any -RemoteAddress <subnets>`;
the group is removed first (idempotent). Both scripts use `$ErrorActionPreference='Stop'` and
single-quote escaping.

**Staleness** (`BanLayersMatch`): the applied NRPT namespaces and firewall subnets must
exactly cover the configured `BanTrees`/`FirewallSubnets` (mask-vs-prefix spellings
normalized). A lagging layer -> `LocalBanStatus.Stale` -> the whole ban reads `Partial`.

**Live probes** (after apply/remove, always): DNS probe (`Dns.GetHostAddressesAsync` —
`HostNotFound`/`NoData` = blocked), direct probe (HTTPS GET with `UseProxy=false`, 6 s connect
/ 8 s total — connection error or timeout = blocked; TLS errors and responses = open; only
reached when DNS resolves), proxied probe (the chain's CONNECT roundtrip — chain down =
skipped). Combined verdict: any open -> open; all blocked -> blocked; else null.

**Elevation**: apply/remove run in the elevated helper (`--elevated-guard-ban-*`) — including
the DNS cache flush (tolerant) and the post-apply DNS probes (warn-only). `BanAsync(false)`
removes each layer independently and raises `BanPartiallyRemovedException` after all attempts
if any layer stayed.

## Error Handling

- An unterminated hosts block is never rewritten.
- A partial ban removal reports which layer stayed (`BanPartiallyRemovedException`).
- A malformed layers-state output (`ParseLayersState`) makes the layer read `Unknown`.
- The firewall service being down is detected up front (`Get-NetFirewallProfile` probe) rather
  than misreported as "no rules".

## Invariants

- The block content is exactly the configured name list in hosts form — no ad-hoc additions.
- Apply/remove of every layer is idempotent (remove-then-add / add-if-different).
- Every ban change ends with the full live probe triple — including a removal.
- The application maintains one ban per `guard.*` identifiers; a foreign rule with another
  name is never touched.
- Profiles do not create parallel bans: both carry the same identifiers and lists, the last
  apply is idempotent, and either profile's enable/disable acts on the one shared ban
  ([Profiles](../configuration/profiles.md)).

## Related Specs

- [Guard](README.md) — status semantics and orchestration
- [Elevated helper](../elevated-helper.md) — where these scripts run
- [Security model](../../architecture/security-model.md) — the layer stack
