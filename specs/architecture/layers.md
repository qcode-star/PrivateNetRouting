# Layers

## Context

PrivateNetRouting is one WPF application plus a headless CLI, both thin hosts over a shared
Core. The layering exists so that every piece of logic is testable without the UI and without
touching the real system: the tests project references Core only and substitutes fakes for
everything that would reach Windows.

## Layer Map

```
+---------------------------------------------------------------+
|  Hosts                                                         |
|  src/PrivateNetRouting.App   (WPF: windows, tray, headless     |
|                               worker/helper roles)             |
|  src/PrivateNetRouting.Cli   (pnr verbs)                       |
+-------------------------------|--------------------------------+
                                | composes, no logic of its own
                                v
+---------------------------------------------------------------+
|  Core  src/PrivateNetRouting.Core                              |
|                                                               |
|  Composition: ProxyRuntime.FromConfig(AppConfig)               |
|                                                               |
|  Modules (depend on each other sideways only through the      |
|  interfaces listed in contracts/system-boundary.md):           |
|    Configuration   Logging     SystemOps (interfaces)          |
|    Net/{Dns,Socks,Proxy}   Runtime (ActivityMarker)            |
|    Chain           Launcher    Updater     Guard               |
|    Watchdog        Diagnostics AppUpdate                       |
+-------------------------------|--------------------------------+
                                | interfaces only
                                v
+---------------------------------------------------------------+
|  System boundary (real OS contact lives in SystemOps           |
|  implementations and module-specific stores):                  |
|    processes/WMI, TCP table, PowerShell, registry, hosts file, |
|    NRPT/firewall, WinINET, AppX/Store, WSL, named pipes        |
+---------------------------------------------------------------+
```

Dependency direction: **hosts -> Core -> system interfaces**. Core never references
`PrivateNetRouting.App` or `PrivateNetRouting.Cli`. Within Core, modules are wired by
`ProxyRuntime` (`src/PrivateNetRouting.Core/ProxyRuntime.cs`); a module reaches another's
behavior through constructor-injected interfaces or delegates, never through static calls
into a sibling module's internals.

## Responsibilities

| Layer | Owns | Never owns |
|---|---|---|
| App | window/tray presentation, dialog flows, UI state persistence (`ui.*` config keys), user confirmations | business logic, direct OS manipulation beyond trivial shell-outs (Explorer open) |
| Cli | argument parsing, exit codes, console prompts, host-side exit orchestration | business logic |
| Core | all behavior: chain orchestration, proxy, guards, launchers, updaters, watchdog, diagnostics | WPF types, console I/O |
| System interfaces | the contract each OS touchpoint must satisfy | policy decisions of when/why to call |

## Initialization

`AppConfigStore.LoadOrCreate()` -> `AppConfig` -> `ProxyRuntime.FromConfig(config)` builds the
whole object graph (proxy, chain, launchers, watchdog, updaters, guard, diagnostics, logs) and
exposes it as one facade. Both hosts consume only that facade plus static Core helpers
(`AppConfigStore`, `AppProxyEndpoint`, `AppLanguage`, worker bodies, elevated-helper commands).
See [Host <-> Core](../contracts/host-core.md).

## Invariants

- Core compiles and its tests run without WPF, WinForms, or console dependencies; the only
  external package of Core is `System.Management` (WMI process lookup).
- Every OS touchpoint sits behind an interface injectable with a fake; the tests project
  references `PrivateNetRouting.Core` only.
- The hosts contain no business rules beyond presentation and exit-code mapping; a rule found
  in a host belongs in Core (known small duplications are tracked in
  [Host <-> Core](../contracts/host-core.md)).
- Composition happens exactly once per process, in `ProxyRuntime.FromConfig`; a config value
  the composition captures at build time is listed in
  [Configuration store](../domains/configuration/store.md) and triggers the restart note on reload.
- Long waits in the WPF host run through async/await off the UI thread; Core start/stop paths
  use `ConfigureAwait(false)` and never capture the caller's `SynchronizationContext`
  (regression test `StopCompletesUnderBlockedSynchronizationContext`).
- Visual properties come from the `App.xaml` token dictionary (semantic brush/font/space keys)
  via `{DynamicResource}`/`Theme`; no window or code-behind hardcodes a color, size, or radius.

## Anti-Patterns

- **A Core module news up a system implementation** (e.g. `new WmiProcessFinder()`) instead of
  taking the interface — untestable and hides the privilege requirement.
- **A host reads or writes config sections directly** outside `AppConfigStore` — the surgical
  patch contract and the restart semantics would be bypassed.
- **Blocking `.Result`/`.Wait()` on Core async work from the UI thread** — deadlock by design;
  hop to the thread pool first (see `Task.Run` usage in `App.OnExit`).
- **Adding a NuGet package to Core** without a written reason in the PR/commit — BCL and the
  `SystemOps` wrappers are the default.
- **A second composition root** (a host building its own `ForwardProxyService` etc.) — the
  facade and its lifecycle contracts would drift between hosts.
