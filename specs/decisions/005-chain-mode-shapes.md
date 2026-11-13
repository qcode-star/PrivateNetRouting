# ADR-005: Chain Mode Shapes Behind One Orchestrator

## Status

Accepted

## Context

The tailnet sidecar is the default egress, but the same application must also work against a
trusted external SOCKS5 upstream (e.g. an existing tunnel) and against a provided plain HTTP
proxy — with the same launchers, guards, watchdog and updater on top. Without a deliberate
shape model, each module grows its own "if tailscale…" special cases and the lifecycle
contracts diverge.

## Decision

`chain.mode` picks one of three shapes — `tailscale` (userspace sidecar), `socks5` (trusted
external SOCKS5 upstream, names ride the CONNECT), `http` (provided CONNECT-capable HTTP
proxy) — and every module branches on the shape through shared derivations instead of its own
logic: `AppProxyEndpoint` (who the clients point at), `ChainState.ProxyRunning = null`
("not applicable" in the skipped shapes), the rail stages (5/3/2), the DoH pool (tailscale
only), the egress-check step (external shapes), the activity marker (skipped where no local
proxy runs). The mode is fixed at composition: a change applies after a restart.

## Consequences

- Positive: one orchestrator, one status vocabulary, one set of lifecycle contracts for all
  three deployments; the "skipped shape" is an honest no-op everywhere instead of a
  half-working proxy.
- Positive: the socks5/http shapes need no UAC, no tailscale processes and no DoH machinery at
  all.
- Negative: three shapes to keep in every per-mode decision (tests cover them as explicit
  matrices — `ChainSupervisorTests`, `ProxyRuntimeTests`, `DiagnosticsTests`).
- Negative: some capabilities genuinely differ per shape (the resolved-IP refusal, the egress
  listener activity signal) and must be documented as such rather than papered over.

## Alternatives Considered

- **Tailscale-only** — rejected: the owner runs deployments where a provided upstream is the
  whole point (the app must not force a tailnet there).
- **Auto-detect the upstream shape from config values** — rejected: implicit shapes make the
  lifecycle unpredictable; an explicit `mode` keeps the contracts testable and the status
  honest.
- **Separate orchestrator per shape** — rejected: the shared idempotence/lock/hook contracts
  would drift (the bug class this ADR exists to prevent).

## Related Specs

- [Chain](../domains/chain/README.md), [Configuration](../domains/configuration/README.md)
