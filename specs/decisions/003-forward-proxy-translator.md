# ADR-003: Local Forward Proxy as the Client Translator

## Status

Accepted

## Context

The clients that must ride the chain speak HTTP CONNECT (Claude Desktop's managed policy,
`HTTP(S)_PROXY` env of Claude Code/Codex) — and the banned names must be resolved *past* the
poisoned system resolver. The tailscale sidecar exposes SOCKS5/HTTP proxies, but its dialer
(ts userspace mode) resolves names with the system resolver — a long-open upstream limitation —
so pointing the clients straight at the sidecar both answers the wrong protocol and resolves
the wrong names.

## Decision

A local HTTP CONNECT proxy (`forwardProxy.listen`) is the single client-facing endpoint. It
translates CONNECT to SOCKS5 toward the sidecar (or the trusted upstream), resolves names
itself over DoH in the tailscale mode (pool auto-filled from the tailnet netmap), refuses
forbidden targets, and is the "applications proxy endpoint" every consumer is pointed at
(`AppProxyEndpoint`). In shapes where translation is unnecessary (the http mode; socks5 with
a mixed inbound) it is skipped and the external endpoint is used directly.

## Consequences

- Positive: names resolve through DoH past the ban; every client speaks its native protocol.
- Positive: one place for the target-refusal rules, the activity marker, and the WSL-facing
  bind.
- Negative: one more process/port to own — mitigated by the supervisor's single-owner rule
  and the detached-worker handover.
- Negative: the socks5 mode's name resolution is delegated to the upstream (documented as that
  mode's contract), so the resolved-IP refusal cannot apply there.

## Alternatives Considered

- **Point clients at the sidecar's own SOCKS5/HTTP proxies** — rejected: system-resolver names
  (the ban blackholes them) and no CONNECT answer for the policy/env clients.
- **Per-client DoH configuration** — rejected: the clients have no such knob; the managed
  policy and the env vars are proxy-address only.
- **Make the upstream resolve and skip the local proxy in the tailscale mode** — rejected: the
  upstream is exactly the sidecar whose resolver is the problem.

## Related Specs

- [Forward proxy](../domains/forward-proxy/README.md), [DoH resolution](../domains/doh-resolution.md)
