# Contract: Hosts <-> Core

## Boundary Rule

The App and the CLI are thin hosts over one facade (`ProxyRuntime`); they present, prompt and
map exit codes — every business rule lives in Core, and both hosts observe the same lifecycle
contracts.

## Interfaces

| Interface | Package | Consumed By | Purpose |
|---|---|---|---|
| `ProxyRuntime.FromConfig(AppConfig)` | `Core` | App, CLI | the whole object graph: `Config`, `ProxyService?`, `ProxyControl`, `Chain`, `Launcher`, `CodexLauncher`, `Browser`, `BrowserProfile`, `Watchdog`, `WatchdogControl`, `Updater`, `CodexUpdater`, `Guard`, `Diagnostics`, `Logs`, `AppUpdate`, `Log` |
| `ProxyRuntime.StartAsync/StopAsync/DisposeAsync` | `Core` | both hosts | proxy lifecycle (dispose on the thread pool from the GUI) |
| `ProxyRuntime.HandOverProxyAsync(ct)` | `Core` | both hosts | hand the in-process listener to a worker under the chain lock |
| `ProxyRuntime.CheckHealthAsync(timeout)` | `Core` | both hosts | the live CONNECT roundtrip |
| `ProxyRuntime.ReloadConfig() -> ConfigNeedsRestart` | `Core` | both hosts | config reload + restart note |
| `ProxyRuntime.RotateLog(fileName)` | `Core` | diagnostics surfaces | manual log rotation |
| `AppConfigStore` (`LoadOrCreate`, `UpdateAsync`, `ReloadInto`, `GetDefaultConfigPath`) | `Core` | both hosts | config access |
| `AppProxyEndpoint` (`IsSkipped`/`Address`/`Url`) | `Core` | both hosts | the applications proxy endpoint |
| `ProxyWorker` / `WatchdogWorker` | `Core` | both hosts | the hidden worker bodies |
| `ElevatedChainCommands` / `GuardElevatedCommands` / `ElevatedHelperArguments` | `Core` | both hosts | the hidden helper dispatch |
| `ForwardProxySupervisor.WorkerMarker` / `WatchdogSupervisor.WorkerMarker` | `Core` | both hosts | the shared identity markers |
| `AppLanguage`, `Loc` (App only) | `Core`/`App` | App | language resolution / string lookup |

## Initialization

`AppConfigStore.LoadOrCreate()` -> `ProxyRuntime.FromConfig(config)`; the App additionally
creates its single-instance events and tray around it. Both hosts dispose the runtime on
exit (the GUI via `Task.Run(...).GetAwaiter().GetResult()` — never on the dispatcher).

## Data Flow Across Boundary

Down: `AppConfig` values and user decisions (consent answers as flags/delegates — e.g.
`LauncherOptions.RestartRunning`, `ConfirmDeadEgress`). Up: result records
(`LaunchResult`, `UpdateRunResult`, `GuardSnapshot`, `DiagnosticsSummary`, `ChainState`,
`ChainStep` progress), typed failures (`ChainException`, `UpdateOutcome.NoChange`), and
one-shot events (`VersionStateResolved`, auth URL on a `ChainStep`).

## Error Propagation

- Cancellation is `OperationCanceledException` end to end; `ChainException` means a failed
  link, never a cancel. The hosts map: failure -> exit 1 / a message box, `NoChange` -> exit 1
  (except a `--force` reinstall), cancel -> exit 130 (CLI).
- Exit-code conventions of the CLI live in [CLI](../domains/cli.md); the GUI surfaces the same
  outcomes as step states and dialogs.

## Breaking Change Checklist

If you change X, you MUST also update Y:

- Change `ProxyRuntime`'s surface or lifecycle -> both hosts' startup/exit paths and this
  contract; run the GUI manually (no automated host coverage).
- Change an outcome enum (`LaunchOutcome`, `UpdateOutcome`, `GuardState`) -> every consumer in
  both hosts (buttons, dots, exit codes) and the localization dictionaries if words changed.
- Change a hidden marker (`--proxy-worker`, `--watchdog-worker`, `--elevated-*`) -> both hosts'
  dispatch (`Program.cs`, `HeadlessHost.cs`), the supervisors' identification logic, and
  `WatchdogSupervisorTests`/`ForwardProxySupervisorTests`.
- Change the handover-on-exit rule -> [CLI](../domains/cli.md) orchestration and
  `App.ExitFromTrayAsync`.

Known host-side duplications (kept deliberately small; a change to one must mirror the other):
the restart-consent policy (CLI console prompt vs `LaunchPrompts` dialog), the ban-verdict rule
for `pnr guard status --live`, and the elevated-helper parsing/dispatch in both hosts.
