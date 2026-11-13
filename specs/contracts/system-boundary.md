# Contract: Core <-> Windows

## Boundary Rule

Core touches the operating system only through the interfaces catalogued here (implemented in
`SystemOps` and module-specific stores); nothing on the Windows side of this table may be
reached without an interface that the tests can fake.

## Interfaces

| Interface | Package | Consumed By | Purpose |
|---|---|---|---|
| `IPortProbe` (`TcpPortProbe`) | `SystemOps` | forward-proxy supervisor, chain | "is this port listening" (no admin) |
| `IProcessFinder` (`WmiProcessFinder`) | `SystemOps` | chain, launchers, watchdog, diagnostics | process scan by name/command line (WMI) |
| `ITcpConnectionTable` (`TcpConnectionTable`) | `SystemOps` | proxy supervisor, diagnostics | port listeners with PIDs (iphlpapi, no PowerShell) |
| `IPowerShellExecutor` (`PowerShellRunner`) | `SystemOps` | guard ban ops, AppX source, journal finder, Codex launcher | windowless PowerShell scripts |
| `IShortcutFactory` (`WScriptShortcutFactory`) | `SystemOps` | browser profile service | desktop `.lnk` creation (WScript.Shell COM) |
| `IElevatedCommandRunner` (`ElevatedRunner`) | `Chain` | chain daemon, guard commands | the runas helper invocation + result file |
| `IProxyStopChannel` (`NamedEventProxyStopChannel`) | `Net/Proxy` | proxy supervisor | session-local stop request to an in-process owner |
| `IDnsResolver` / `IDohEndpointSource` | `Net/Dns` | forward proxy, DoH pool | name resolution (faked as `FakeResolver`, `BlockingDohSource`) |
| `ISidecarDaemon` / `ISidecarKeeper` / `ISidecarCli` | `Chain` | ChainSupervisor | the tailscale sidecar surface |
| `IProxyControl` / `IForwardProxyInstance` / `IProxyStatistics` | `Net/Proxy`, `Chain` | chain, hosts | proxy lifecycle + counters |
| `IAppxPackageSource` (`PowerShellAppxPackageSource`) | `Updater` | Claude updater | Get/Add-AppxPackage |
| `IUpdateUrlFinder` (`UpdateFeedUrlFinder`, `PowerShellJournalUrlFinder`) | `Updater` | Claude updater | package URL discovery |
| `IMsixDownloader` (`FragmentDownloader`) | `Updater` | Claude updater | chunked download |
| `IStoreUpdateSource` / `IStoreInstallHandle` (`StoreUpdateSource`) | `Updater` | Codex updater | WinRT AppInstallManager |
| `ICodexVersionManifest` (`CodexVersionManifest`) | `Updater` | Codex updater | offered-version manifest |
| `IClaudeProcessSource` / `IClaudeProcessHandle` (`RunningClaudeProcessSource`) | `Updater` | both updaters | running app handles for the close |
| `IRunningAppCloser` (`WindowsRunningAppCloser`) | `Launcher` | launchers, updaters | graceful close + tree kill |
| `IEgressPolicyStore` (`RegistryEgressPolicyStore`) | `Guard` | GuardService | HKLM managed policy |
| `IBanSystemOps` (`PowerShellBanSystemOps`) | `Guard` | GuardService | NRPT + firewall |
| `ISystemProxyStore` (`WinInetSystemProxyStore`) | `Guard` | GuardService | WinINET proxy values |
| `IClaudeUsageProbe` / `IEstablishedConnectionCounter` | `Watchdog` | WatchdogService | busy/idle signals |
| `IWslProcessRunner` (`WslProcessRunner`) | `Watchdog`/`Guard` | WSL stopper, WSL guard | `wsl.exe` / in-distro bash |
| `IAppReleaseFeed` (`GitHubAppReleaseFeed`) | `AppUpdate` | app self-update | GitHub releases |
| `TimeProvider` | BCL | marker, DNS cache, downloads | injectable clock |

Static system helpers (not interfaces, but boundary code): `ProcessOps` (cmd launching with
file redirection that outlives the app, handle pinning), `WindowsPrivilege`, `ProcessMatch`
(PID-reuse-safe matching/kills), `AtomicFile`, `CommandLineSafety`, `ElevatedHelperArguments`,
`ElevatedHelperTrust`, `WslGateway`.

## Initialization

`ProxyRuntime.FromConfig` constructs every implementation above with its config-derived
parameters and injects them into the modules; the hosts never build a system implementation
directly. The tests substitute fakes — some shared in `tests/PrivateNetRouting.Tests/Fakes.cs`
(`FakeFinderByName`, `FakeResolver`, `FakeAppCloser`, `FakeAppReleaseFeed`, `ManualTimeProvider`,
`BlockingDohSource`), many defined alongside the tests that need them
(`FakePortProbe`, `FakeProxyInstance`, `FakeFinderById`, `FakeTcpTable`, …).

## Data Flow Across Boundary

Out: command lines and scripts (PowerShell/bash/cmd), registry values, hosts-file blocks,
socket operations, WSL invocations. In: process lists (`FoundProcess(Id, CommandLine)`), TCP
tables (`TcpListenerEntry(Address, Port, ProcessId, ProcessName)`), script outputs `(exitCode,
stdout+stderr)`, file bytes, AppX/Store state, stdout of `wsl.exe`/bash (always parsed through
marker rules like `PNR=ok`).

## Error Propagation

- Privileged operations declare their requirement in a documenting comment (UAC/admin) and are
  reached only through `IElevatedCommandRunner` where elevation is needed.
- A system call failure surfaces as a typed exception at the module boundary
  (`IOException`/`InvalidDataException`/`ChainException`); the elevated helper translates
  everything into `ElevatedResult(Ok, Detail)`.
- Timeouts on system calls kill the child tree and throw — never leave a half-running child.

## Breaking Change Checklist

If you change X, you MUST also update Y:

- Add/rename/remove an interface above -> update the fakes (shared and local), the
  `ProxyRuntime` wiring, and this table.
- Change a command line/script shape -> update the `ProcessMatch`/marker expectations and the
  affected tests (`TailscaleCommandLineTests`, `GuardTests`, `WatchdogTests`).
- Change `ElevatedHelperArguments`/`ElevatedHelperTrust` shapes -> update both hosts' helper
  dispatch (`Program.cs`, `HeadlessHost.cs`) and `ElevatedHelperTests`.
- Change the `PNR=ok` / completion-marker convention -> update every WSL-bound builder and its
  tests (LF normalization included).
