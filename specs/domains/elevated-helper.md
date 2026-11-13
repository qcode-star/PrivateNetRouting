# Elevated Helper

## Purpose

The single elevation mechanism of the system: the application launches a copy of itself with
`runas` (the only UAC prompt) to execute one marked command — chain daemon start/stop or guard
ban/policy apply/remove — and reads the outcome from a result file. Everything a helper takes
on trust from the user-writable environment is validated and pinned.

## Key Files

- `src/PrivateNetRouting.Core/Chain/ElevatedChain.cs` — `ElevatedRunner`, `ElevatedChainCommands` (`--elevated-start-daemon`, `--elevated-stop-chain`), `ElevatedResult`, `IElevatedCommandRunner`
- `src/PrivateNetRouting.Core/Guard/GuardElevatedCommands.cs` — `--elevated-guard-ban-on/off`, `-policy-on/off`, `-all-on/off`
- `src/PrivateNetRouting.Core/SystemOps/ElevatedHelperArguments.cs` — command-line build/parse, `--config`/`--env` profile variables
- `src/PrivateNetRouting.Core/SystemOps/ElevatedHelperTrust.cs` — pinned locations, result-file shape, atomic result writes

## Core Types

```csharp
public interface IElevatedCommandRunner {
    Task<ElevatedResult> RunAsync(string command, CancellationToken ct);
}
public sealed record ElevatedResult(bool Ok, string Detail);

public static class ElevatedHelperArguments {
    // <command-marker> --result-file "<path>" [--config "<path>"] [--env "NAME=value"]…
    public static string Build(string command, string resultFile, string? configPath,
                               IReadOnlyDictionary<string, string>? profileEnvironment);
    public static bool IsHelperCommand(string? arg);   // --elevated-* markers
}
```

## Flow

```
parent (unelevated)                     elevated copy of the same exe
  |  build command line (--result-file, --config, --env)     |
  |-- Process.Start(Verb="runas") -------------------------->|
  |                                  validate: result file shape, config path, stateDir
  |                                  pin system binaries/paths
  |                                  execute the command body (chain / guard)
  |                                  write result file (ok <detail> | fail <message>), atomic
  |<-- poll result file every 500 ms (uacWaitSeconds bound) --|
  cancel while waiting -> drop "<result>.cancel" marker -> helper aborts/kills what it started
```

## Behavior

**Invocation** (`ElevatedRunner.RunAsync`): already elevated (or bypassed) -> run in-process on
the thread pool (no UAC); else self-exe via `Process.Start` with `UseShellExecute=true,
Verb="runas"`, hidden window. A declined UAC (`Win32Exception`) becomes
`ElevatedResult(false, "UAC declined: …")`.

**Command line**: `<command-marker> --result-file "<path>" [--config "<path>"] [--env
"NAME=value"]…`. `--env` carries only the asking user's `APPDATA`/`LOCALAPPDATA`/`USERPROFILE`
(validated: absolute, no cmd metacharacters/`%`, no trailing backslash, exact names — anything
else is dropped/refused) so the helper expands paths as the asking user, not as the
administrator.

**Result file**: under the pinned `%ProgramData%\PrivateNetRouting\elevated` directory, name
shape `<command>=<8-hex-nonce>.result`; content `ok <detail>` / `fail <message>`, written
atomically (temp + rename). The parent polls every 500 ms up to `uacWaitSeconds`; a
`.cancel` suffix marker aborts or kills an in-flight helper; results older than a day are
swept.

**What the helper refuses**: a result file outside the pinned shape, a non-absolute/non-`.json`
config path, a stateDir that is not an absolute inert local path ("refusing to create it as
administrator"). The chain helper pins the system Tailscale install and the system PowerShell
(a differing `powerShellPath`/`logging.directory` is warned and ignored); the guard helper
pins the system hosts file and PowerShell (`PinSystemLocations`).

**Command bodies**:
- `--elevated-start-daemon` — cancel-marker check, binary pinning, dev flag from the pinned
  binary's version, daemon start, listener wait (`daemonWaitSeconds`); a mid-start cancel kills
  the daemon it started.
- `--elevated-stop-chain` — optional `tailscale down` via the pinned CLI (skipped with a
  warning when absent), kill by statedir/socket, verify, "still there" -> failure.
- `--elevated-guard-*` — hosts block apply/remove, NRPT rule, firewall group, DNS cache flush
  (tolerant), HKLM policy apply/remove, and after a ban-on: warn-only DNS probes of each
  configured probe name.

## Error Handling

- Every command writes exactly one result file; failures are `fail <message>` content, not
  silent exits.
- A refusal is an immediate non-zero exit with the reason in the result file.
- `BanPartiallyRemovedException` (guard ban-off): every layer is attempted, then the
  aggregated failure is raised.
- Cancellation while waiting surfaces as `OperationCanceledException` and drops the `.cancel`
  marker so the helper aborts what it started.

## Invariants

- The privileged surface is exactly the commands above — nothing else runs elevated.
- A user-writable config value never names a binary or path the elevated side executes;
  system locations are pinned.
- The result file is the only channel from helper to parent; it lives in the pinned directory
  and matches the expected name shape.
- An already elevated host runs the command in-process — no nested UAC.
- Helper logs go to the pinned `chain-elevated.log` regardless of the user's logging config.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `chain.tailscale.uacWaitSeconds` | 90 | how long the parent polls the result file |
| `logging.directory` | — | ignored in the helper (its log is pinned) |
| `powerShellPath` | system PowerShell | overridden to the system one in the helper |

The pinned helper directory (`%ProgramData%\PrivateNetRouting\elevated`) and the result-file name
shape are code constants (`ElevatedHelperTrust`), not config.

## Extension Points

- A new helper command: add the `--elevated-*` marker, teach `ElevatedHelperArguments.IsHelperCommand`
  and the hosts' dispatch (`Program.cs`, `HeadlessHost.cs`) about it, validate every new argument
  through `ElevatedHelperTrust`, and cover the refusal paths in `ElevatedHelperTests`.

## Related Specs

- [Security model](../architecture/security-model.md) — why the surface is this narrow
- [Chain sidecar](chain/sidecar.md) — the daemon commands' semantics
- [Guard](guard/README.md) — the guard commands' semantics
- [System boundary](../contracts/system-boundary.md) — `IElevatedCommandRunner` in the interface catalog
