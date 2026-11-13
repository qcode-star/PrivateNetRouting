# ADR-004: Three-Layer Local Ban Plus Managed Policy

## Status

Accepted

## Context

The proxy chain only works if direct access to the AI service names actually fails — otherwise
a single misconfigured or hostile client leaks around it. The names are of two kinds (finite
known hosts, and wildcard DNS trees), the vendors run on shared clouds as well as their own
networks, and the OS offers several blunt instruments with very different coverage.

## Decision

One managed ban with four cooperating parts, all keyed on configurable identifiers: the
Claude managed policy (`egressProxyUrl`), a **hosts block** for the finite names, **one NRPT
rule** for the wildcard subtree apexes (resolved to `127.0.0.1`), and **firewall outbound
Block rules** by destination subnet where the vendor owns its ranges. A layer that lags the
config makes the ban `Partial`, never "on"; every apply/remove ends in live DNS/direct/proxied
probes.

## Consequences

- Positive: defense in depth — each layer catches what the others cannot (hosts cannot hold a
  wildcard; the firewall catches direct-by-IP; the policy covers the desktop app itself).
- Positive: the status model is honest ("on" requires all layers current) and verifiable live.
- Negative: three Windows surfaces to maintain and elevate for (all behind one helper
  command), and shared-cloud IPs cannot be banned by subnet (accepted: those rides go through
  the proxy or not at all).
- Negative: a false-positive probe name would look like a ban hole — the probe names are
  therefore required to sit inside the ban trees but outside the hosts lists.

## Alternatives Considered

- **hosts file only** — rejected: finite names cannot cover wildcard trees
  (e.g. the user-content wildcard), and a missing subdomain is a silent leak.
- **Firewall by IP only** — rejected: the vendors use shared clouds/CDNs where a destination
  range ban would block unrelated traffic (the Codex list is deliberately empty for this
  reason).
- **DNS-only (a poisoned resolver / NRPT only)** — rejected: an app that resolves elsewhere
  (DoH itself, hardcoded IPs) walks past it.
- **A driver-level block** — out of scope by [ADR-001](001-routing-without-drivers.md).

## Related Specs

- [Guard](../domains/guard/README.md), [Ban layers](../domains/guard/ban-layers.md),
  [Security model](../architecture/security-model.md)
