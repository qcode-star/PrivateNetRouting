# ADR-001: Routing Without Drivers or Process Injection

## Status

Accepted

## Context

Claude/Codex traffic must never leave the machine directly: the direct-access names are
blackholed for the system resolver and the egress must be pinned to one proxy chain. The
obvious industrial answer — intercept every process's sockets — was on the table and had to be
decided explicitly, because it shapes the whole product.

## Decision

All routing is solved by the proxy and the application's policies only: the local CONNECT
proxy, the managed policy, the hosts/NRPT/firewall ban, and the settings sync. No kernel
drivers, no WFP callout drivers, no DLL injection, no LSP/WinSock providers — and none may be
proposed as an improvement (AGENTS.md rule #5 states this as a project rule).

## Consequences

- Positive: no stability or antivirus risk from drivers/injection; no per-machine kernel
  footprint; the whole system stays a per-user install; failures are debuggable as ordinary
  processes and files.
- Positive: the threat model stays simple — a proxy plus policy, both inspectable in the
  guard status.
- Negative: a program that hardcodes a direct connection and ignores proxy settings can bypass
  the chain (mitigated, not eliminated, by the local ban layers — and that is accepted).
- Negative: no transparent interception means every client must be pointed at the proxy
  explicitly (flags, env, policy, profile).

## Alternatives Considered

- **Proxifier-like LSP / WinSock interception** — rejected: injection into every process,
  fragile across updates, flagged by AV, and a per-machine change.
- **WFP callout driver** — rejected: kernel code for a per-user tool; signing and stability
  cost with no acceptable payoff here.
- **DLL injection into the clients** — rejected: breaks on every client update and violates
  the same principle.

## Related Specs

- [Security model](../architecture/security-model.md)
