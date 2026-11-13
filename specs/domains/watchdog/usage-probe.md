# Usage Probe

## Role

`ClaudeUsageProbe` — decide whether anything is using the chain: processes, connections to the
proxy, connections to the chain egress listener, and the activity marker's freshness.

## Key Files

- `src/PrivateNetRouting.Core/Watchdog/ClaudeUsageProbe.cs` — `IClaudeUsageProbe`, `ClaudeUsageProbe`, `IEstablishedConnectionCounter`, `TcpEstablishedConnectionCounter`

## Behavior

`GetInUseReason(idleWindow)` checks in order, first hit wins:

1. **Processes** — `FindByNameContains("claude")` count, plus processes matching
   `codex.processName` filtered by the Codex install-folder command-line marker (a foreign app
   sharing the process-name spelling is not counted; an empty marker falls back to the name).
   Any > 0 -> busy with that reason.
2. **Connections to the application proxy endpoint** — established count on the local port
   (local-end), or by remote endpoint when the applications endpoint is external and
   non-loopback (`CountToRemote`).
3. **Connections to the chain egress listener** — established count on
   `chain.tailscale.socksListen` (tailscale mode only; port 0 elsewhere means the signal is
   skipped — a browser pointed there keeps the chain alive).
4. **Activity marker freshness** — file exists and `age < idleWindow` and `age > -5 min` (a
   far-future stamp is no proof). Skipped entirely (null path) in skipped shapes; a missing or
   unreadable marker is simply no signal.

**Fail-closed**: any source that throws is recorded as a failure and the verdict is "probe
failed (…), assuming in use"; only an all-answered idle returns null.

Connection counting is listener-side only (`LocalEndPoint.Port`, Established state).

## Error Handling

- A throwing signal -> busy (fail-closed), with the reason naming the failed source.
- A scan/connection failure never crashes the loop.

## Invariants

- Priority order is fixed: processes > proxy connections > egress connections > marker.
- A marker stamped in the future beyond the tolerance window proves nothing.
- The marker signal is absent (not "idle") in shapes where no local proxy runs.
- A foreign process that merely shares a name spelling never keeps the chain alive.

## Related Specs

- [Watchdog](README.md) — the loop consuming this verdict
- [Activity marker](../activity-marker.md) — the freshness source
- [Forward proxy](../forward-proxy/README.md) — the proxy connection source
