# Configuration

## Purpose

The single source of every tunable: `AppConfig` — a POCO tree with coded defaults, loaded from
the user config, validated once, and read by every module at composition. Also owns the
"applications proxy endpoint" derivation (`AppProxyEndpoint`), the chain/elevation enums, and
the UI language resolution.

## Key Files

- `src/PrivateNetRouting.Core/Configuration/AppConfig.cs` — the model: `AppConfig` + section classes + coded defaults
- `src/PrivateNetRouting.Core/Configuration/AppConfigValidator.cs` — one validation pass with per-key messages
- `src/PrivateNetRouting.Core/Configuration/AppProxyEndpoint.cs` — `IsSkipped`/`Address`/`Url`/`EndPoint`/`Port`
- `src/PrivateNetRouting.Core/Configuration/ChainMode.cs` — `Tailscale`/`Socks5`/`Http` + parser (unknown -> `Tailscale`)
- `src/PrivateNetRouting.Core/Configuration/DaemonElevation.cs` — `Auto`/`Never`/`Always` + parser (unknown -> `Auto`)
- `src/PrivateNetRouting.Core/Configuration/AppLanguage.cs` — `Auto`/`Fallback`/`Supported`/`Resolve`

## Core Types

```csharp
public sealed class AppConfig {
    public List<string> BypassDomains;       // zones -> two spellings everywhere
    public List<string> DirectTlds;          // *.tld never leaves the chain exit
    public int RestartGraceSeconds = 15, CloseWaitSeconds = 25, KillSettleSeconds = 3;
    public string PowerShellPath;            // %WINDIR%…powershell.exe
    public UiConfig Ui; public LoggingConfig Logging;
    public ClaudeConfig Claude; public CodexConfig Codex;
    public ForwardProxyConfig ForwardProxy; public ChainConfig Chain;
    public WslConfig Wsl; public WatchdogConfig Watchdog;
    public BrowserConfig Browser; public GuardConfig Guard;
    // computed unions: BanHosts, BanTrees, FirewallSubnets (Claude ∪ Codex)
}

public static class AppProxyEndpoint {
    // The "applications proxy" address for every consumer (launchers, guard sync,
    // updater, health checks, browser profile).
    public static bool IsSkipped(AppConfig c);   // http mode, or socks5 + proxy off
    public static string Url(AppConfig c);       // scheme://host:port
}
```

## Flow

```
LoadOrCreate (%APPDATA%\PrivateNetRouting\appsettings.json; created from code defaults,
               saved unexpanded) -> env-var expansion (path keys only) -> validate
-> ProxyRuntime.FromConfig(config)   (composition captures a listed subset)
-> reads at runtime; UI writes via AppConfigStore.Update (see store.md)
```

`AppProxyEndpoint`: the local forward proxy's `listen` address in the default shapes; the
external upstream itself in the skipped shapes (`chain.socks5.upstream` for socks5+off,
`chain.http.upstream` for http). Every consumer takes its address from here.

## Invariants

- Nothing environment-specific is hardcoded: addresses, ports, paths, distro names, bypass
  lists and timeouts all live in the user config (rule #3). The repository ships placeholders
  only (`config/appsettings.example.json`).
- The first-run config file keeps `%VAR%` unexpanded (no user name leaks into the file); the
  in-memory copy is expanded.
- Endpoints are `IP:port` literals — a host name is rejected by the validator.
- An unknown enum string falls back to the documented default (`ChainMode` -> tailscale,
  elevation -> auto, log level -> info, egress -> forward-proxy).
- The validator names the offending key in its message and refuses null sections, bad
  endpoints (of the active mode only), non-inert tailscale paths, and non-positive timings.

## Configuration

The authoritative schema with comments is `config/appsettings.example.json`; the full key
inventory with defaults is documented per module:
[forward proxy](../forward-proxy/README.md), [chain](../chain/README.md),
[guard](../guard/README.md), [watchdog](../watchdog/README.md),
[updater](../updater/README.md), [browser](../browser/README.md),
[logging](../logging.md), plus top-level keys above.

## Extension Points

- A new setting: add it to `AppConfig` with a coded default, to
  `config/appsettings.example.json` with a comment, and only then read it (rule #3). If the
  composition captures it at build time, add it to the restart-note list in
  [store.md](store.md).
- A new mode value: extend the enum **and** its parser's fallback, and teach
  `AppProxyEndpoint`, the chain rail and the validator about it.

## Related Specs

- [Configuration store](store.md) — load/save/patch/reload semantics
- [Host <-> Core](../../contracts/host-core.md) — who composes from this config
- [Security model](../../architecture/security-model.md) — what may and may not live in it
