# Codex Launcher

## Role

Start the Codex desktop app (a Microsoft Store MSIX) with the proxy on every path the packaged
stub allows: proxy env + flags in a package-context launch, and a refreshed `CODEX_HOME/.env`
for the embedded CLI core.

## Key Files

- `src/PrivateNetRouting.Core/Launcher/CodexLauncher.cs` — `CodexLauncher`, `BuildLaunchScript()`
- `src/PrivateNetRouting.Core/Guard/CodexEnvFile.cs` — the `.env` merge/refresh (shared with the guard sync)

## Behavior

Shared skeleton with the Claude launcher (live CONNECT -> chain up on failure -> CheckOnly ->
running-copy warning/restart -> report), then:

1. **`.env` refresh** — `CodexEnvFile.Write(config)` before every launch. The file carries
   `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` = the AppProxyEndpoint URL and `NO_PROXY` from the
   bypass zones; the launcher restores the Windows form after a WSL session rewrote it to the
   NAT-gateway form. Unchanged content is not rewritten (backups still pruned).
2. **Launch script** (`BuildLaunchScript`, run through PowerShell): sets the four proxy env
   variables for children that inherit them, resolves the package
   (`Get-AppxPackage -Name <codex.packageName>`; absent -> `IOException`), then
   `Invoke-CommandInDesktopPackage -PackageFamilyName … -AppId <codex.appId> -Command
   <InstallLocation>\<codex.appExecutable> -Args '<proxy flags>'`, printing `PNR=ok`. The
   flags are the same Chromium pair as Claude's (`--proxy-server`, `--proxy-bypass-list`).
   Non-zero exit -> `IOException`.

Why the detour: the packaged stub loses arguments and a DCOM activation loses the
environment — only the package-context launch delivers both to the real entry point
(`app\ChatGPT.exe` by default).

## Error Handling

- Package absent -> `IOException` naming the package name.
- Script failure -> `IOException` with the PowerShell output.
- An already running copy without flags -> the standard warn/restart contract
  ([Running app](running-app.md)); marker = `codex.packageName` in the command line.

## Invariants

- Both belt paths carry the same endpoint: the command-line flags and the env variables name
  the AppProxyEndpoint; the `.env` file is the authoritative one for the embedded CLI core.
- The `.env` merge is surgical: foreign lines survive byte for byte, newline style is kept,
  maintained keys collapse to canonical uppercase `KEY=value`.
- No chain, no proxy state is assumed beyond the shared launch skeleton.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `codex.packageName` / `appId` / `appExecutable` | Store package / `App` / `app\ChatGPT.exe` | launch context |
| `codex.processName` | `ChatGPT` | already-running + update close |
| `codex.envPath` | `%USERPROFILE%\.codex\.env` | env vars expanded |
| `codex.settingsPath` | `%USERPROFILE%\.codex\config.toml` | the `respect_system_proxy` flag lives here |

## Related Specs

- [Launchers](README.md) — the shared skeleton
- [Guard settings sync](../guard/settings-sync.md) — the same `.env` shape maintained by the sync
- [Proxied apps](../../contracts/proxied-apps.md) — the maintained-file contract
