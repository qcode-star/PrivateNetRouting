# ADR-002: Userspace Sidecar With Private State

## Status

Accepted

## Context

The tailscale mode needs a tailnet node on this machine. The machine may also run (or later
run) the system-wide Tailscale installation with its own account, tailnet and state. The
application must not disturb that, must not require a tailnet account of its own beyond the
owner's node, and must control exactly which DNS and egress the proxied apps see.

## Decision

The chain runs its **own** userspace tailscaled (`--tun=userspace-networking`) with a private
statedir and named pipe under `%APPDATA%\PrivateNetRouting`, its own keeper and CLI attachment,
and its own SOCKS5/HTTP listeners. The standard per-machine install is used only as the
binary source for the elevated path (pinned, version-checked) and as a read-only
`ControlURL` hint for the first login. The system daemon, if any, is never started, stopped,
logged into, or reconfigured.

## Consequences

- Positive: the app's tailnet node, keys and state are isolated; uninstalling the app removes
  the node with its folder; the system-wide Tailscale keeps working untouched.
- Positive: the userspace engine needs no driver and fits the per-user install; the DNS
  limitation of its dialer is compensated by our DoH resolution
  ([ADR-003](003-forward-proxy-translator.md)).
- Negative: two tailscale processes can coexist (memory cost) and the node requires its own
  login to the tailnet.
- Negative: the elevation story must deal with pipe ownership across tailscale versions
  (the `1.104.1` threshold and `--windows-mode=dev`).

## Alternatives Considered

- **Attach to the system-wide daemon/CLI state** — rejected: couples the app to the machine's
  tailnet identity, risks `up`/`down` against the owner's node, and gives no control over the
  DNS the proxied apps see.
- **Require the system Tailscale as a hard dependency** — rejected: the app must work on a
  machine without it (and in the socks5/http modes nothing tailscale-shaped runs at all).

## Related Specs

- [Chain](../domains/chain/README.md), [Chain sidecar](../domains/chain/sidecar.md)
