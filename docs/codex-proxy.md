# Running the Codex desktop app through the proxy (Windows and WSL)

The Codex desktop app (OpenAI, a Microsoft Store MSIX) is an Electron/Chromium shell with an
embedded CLI core — two separate processes with separate network stacks, and they need different
mechanisms to go through a proxy: the Chromium flags cover only the shell. On top of that, the app
can run codex inside a WSL distro for WSL projects, which needs a layer of its own.

The guide uses the application's default proxy address `http://127.0.0.1:18081` — substitute your
own. The bypass-list construction (base entries, both spellings of every zone, the single-instance
restart trap) is the same as Claude's: see [claude-proxy.md](claude-proxy.md).

## The trap: the core does not inherit the proxy

The desktop app strips the standard proxy environment variables
(`HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`/`NO_PROXY`) from the processes it spawns. Setting them at
launch reaches the app's own process, not the core. The symptom is split behaviour: the Chromium
UI works through the proxy flags, while login fails with `Token exchange failed: error sending
request` — the core went direct and hit the block.

## The fix: the `.env` of CODEX_HOME

The core loads `<CODEX_HOME>/.env` **after its own start** and applies the values over the
inherited environment (only `CODEX_*` names are protected from it) — so this file is what
actually carries the proxy to the core. The default `CODEX_HOME` is `%USERPROFILE%\.codex`; the
file is plain `KEY=value` lines (an `export KEY=value` spelling is recognized too):

```text
HTTP_PROXY=http://127.0.0.1:18081
HTTPS_PROXY=http://127.0.0.1:18081
ALL_PROXY=http://127.0.0.1:18081
NO_PROXY=localhost,127.0.0.1,.local,corp.example.com,.corp.example.com
NODE_USE_ENV_PROXY=1
```

Keep every foreign line of the file intact and rewrite only the maintained lines.
`NODE_USE_ENV_PROXY` is what makes Node's built-in HTTP clients honour the proxy env; Codex's
browser and computer-use helpers are Node programs, so the line belongs with the proxy env even
though it is not an address. Refresh the file before every launch: a WSL codex session rewrites it
to the WSL gateway address (below), which is unreachable from Windows once WSL is down — with a
stale address the core would silently stop reaching anything.

## config.toml: respect_system_proxy must be off

`%USERPROFILE%\.codex\config.toml` (owned by the app itself; a `[features]` section is typically
absent until added):

```toml
[features]
respect_system_proxy = false
```

The flag must stay **false** (or absent — false is the default). With `true` Codex resolves the
system proxy first; when the system proxy is disabled the route resolves to direct and the client
calls `no_proxy()`, so the proxy env of `.env` is **ignored**. The core then goes direct: login
fails with `Token exchange failed: error sending request for url
(https://auth.openai.com/oauth/token)` (`is_connect=true` in the log), and the desktop's
account/workspace lookup fails (`authenticatedAccountPresent=false`; the workspace routes answer
432 "Workspace routing is unavailable"). With `false` the core keeps reqwest's default proxy
behavior, which reads `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` from `.env` — that is the transport
this setup relies on. Do not turn the flag on "just in case": on a machine without a system proxy
it silently disables the `.env` layer.

The app rewrites the file on its own, so the edit must be surgical: every other line survives byte for byte, the
newline style is kept, the key is inserted into an existing `[features]` section (or one is
appended), and the top-level dotted spelling `features.respect_system_proxy = false` is an
equivalent form.

## config.toml: the managed proxy of the sandbox

The app's own transport and the commands the model runs in the sandbox are two different paths.
For the second one newer builds ship a managed network proxy: Codex starts it when the selected
permission profile lets the sandbox use the network, forces the command traffic through it, and
points the command environment at it. With `allow_upstream_proxy` the managed proxy forwards to
the proxy env it inherited — which `.env` has already set to the application's endpoint:

```toml
[features.network_proxy]
enabled = true
allow_upstream_proxy = true
enable_socks5 = false

[features.network_proxy.domains]
"*" = "allow"
```

The allow-everything domain entry makes this a chaining layer, not a filter. Whether the sandbox
has network at all stays the permission profile's decision — do not add `default_permissions` or a
`[permissions]` table for this; the app's own network toggle supplies the profile. The managed
proxy's default listener (`127.0.0.1:3128`, next free port if taken) is local to Codex and is not
something the client points at. Local and private destinations remain blocked by the managed proxy
(`allow_local_binding` stays at its default `false`), so loopback is not a way around the chain; a
command that needs an intranet host must be handled deliberately, not by disabling that check.

## Launching with the flags: the MSIX specifics

Of the ways to start the app, only one delivers the arguments:

- the packaged entry-point stub (`Codex.exe`) **loses command-line arguments** — the flags never
  arrive;
- a plain activation (Shell/DCOM) loses the environment set at launch;
- the package-context start works:

```powershell
$pkg = Get-AppxPackage -Name 'OpenAI.Codex'
$exe = Join-Path $pkg.InstallLocation 'app\ChatGPT.exe'   # the manifest entry point
Invoke-CommandInDesktopPackage -PackageFamilyName $pkg.PackageFamilyName -AppId 'App' `
    -Command $exe -Args '--proxy-server=http://127.0.0.1:18081 --proxy-bypass-list=<list>'
```

`Invoke-CommandInDesktopPackage` starts the app as a child of the PowerShell process, so the
environment of that shell reaches the app — setting the proxy env there before the call is a
belt-and-braces path for any Codex child that does inherit it (the core still needs the `.env`
above: the app strips the variables from its children).

The app is single-instance like Claude: a running copy swallows a new start, so close it first
(gracefully, then forced after a grace period) before starting it with the flags.

## Codex inside WSL

For WSL projects the desktop app runs codex inside the distro — a login shell
(`wsl.exe -d <distro> --exec /usr/bin/bash -lc …`), where `127.0.0.1` is the distro itself and
none of the Windows proxies exist; without a layer there, a WSL project goes direct.

What distinguishes a shell Codex started from an ordinary one: the desktop app always passes
`CODEX_HOME` into the distro for its own processes (the `codex` command name is the fallback
marker). A `/etc/profile.d/` snippet exports the proxy only to those shells — ordinary terminals,
apt, pip and git keep the direct route:

```bash
# /etc/profile.d/codex-proxy.sh — proxy env for the shells Codex itself starts.
PORT=18081
_codex_shell=0
[ -n "${CODEX_HOME:-}" ] && _codex_shell=1
case "${BASH_EXECUTION_STRING:-}" in
    *codex*|*Codex*) _codex_shell=1 ;;
esac
if [ "$_codex_shell" = "1" ]; then
    # The NAT gateway address changes with every WSL restart — recompute it every time.
    _host_ip=$(ip route show default 2>/dev/null | awk '{print $3; exit}')
    if [ -n "$_host_ip" ]; then
        export HTTP_PROXY="http://$_host_ip:$PORT"
        export HTTPS_PROXY="$HTTP_PROXY"
        export ALL_PROXY="$HTTP_PROXY"
        export NODE_USE_ENV_PROXY=1
        export NO_PROXY="localhost,127.0.0.1,.local,corp.example.com,.corp.example.com"
        # The exports alone are not enough: the core loads "$CODEX_HOME/.env" over the
        # inherited environment, so the loopback form kept there would win — rewrite it.
        _env="$CODEX_HOME/.env"
        if [ -f "$_env" ]; then
            sed -i \
                -e "s#^\(export \)\{0,1\}HTTP_PROXY=.*#HTTP_PROXY=$HTTP_PROXY#" \
                -e "s#^\(export \)\{0,1\}HTTPS_PROXY=.*#HTTPS_PROXY=$HTTP_PROXY#" \
                -e "s#^\(export \)\{0,1\}ALL_PROXY=.*#ALL_PROXY=$HTTP_PROXY#" \
                -e "s#^\(export \)\{0,1\}NO_PROXY=.*#NO_PROXY=$NO_PROXY#" "$_env"
        fi
    fi
fi
```

The forward proxy must listen on the WSL gateway address for any of this to work — see
[the WSL section of the Claude guide](claude-proxy.md#claude-code-inside-wsl). The Windows side
refreshes the `.env` back to the loopback form before every launch, so the two directions do not
fight over the file. (The sed above is the minimal shape: it rewrites existing lines only. The
application's version of the same merge also adds missing keys and preserves foreign lines.)

The guard also maintains the flag `respect_system_proxy = false` and the managed-proxy block in
the distro's own `~/.codex/config.toml` (`[features.network_proxy]` plus the allow-everything
domain entry), so the codex core running inside WSL gets the same transport as the Windows one:
the flag off makes it read the proxy env the snippet exported, and its managed proxy chains to
the gateway address the snippet exported. A `features` inline table or dotted
`features.network_proxy.*` keys make that distro's sync fail with the reason instead of
rewriting the file.

## Updates

The app updates through the Microsoft Store; the Store's own CDN needs neither the proxy nor
elevation. The only piece that can sit behind a direct-access block is the provider's version
manifest — ask for it through the live proxy when in doubt.

## Verification

- The shell: connections from the app's processes to the proxy port (the TCP table, or the
  proxy's own connection counter).
- The core: a login succeeds — before the `.env` layer it fails with the token-exchange transport
  error while the UI looks fine. The core keeps a log database (`~/.codex/logs_2.sqlite`) that
  shows live responses instead of send errors.
- The transport, in one command: the bundled core's `codex doctor` (the `app\resources\codex.exe`
  of the installed package) prints a `Connectivity` section with the proxy env vars it loaded, the
  `respect system proxy` state and a live Responses WebSocket handshake. The state must read
  `disabled`: with `enabled` the core is on the system-proxy path and `.env` is not the transport.
  `HTTP 101 Switching Protocols` next to a failed DNS lookup is the clean result under a local ban:
  the handshake went through the proxy, and the system resolver never saw the blocked name. This
  WebSocket is the transport that silently times out with only a system proxy configured
  ([openai/codex#63294](https://github.com/openai/codex/issues/63294)); a failed handshake while
  the env vars are listed means the proxy is not reachable, and missing env vars mean the `.env`
  layer is not reaching the core.
- Commands the model runs in the sandbox: with the network block above, `codex doctor` reports
  `managed proxy configured` only while a permission profile with network enabled is selected;
  under a profile without network it says `not configured` even though the block is present, and
  nothing starts — that difference is expected. Command traffic is a separate path from the
  app's own transport and is not covered by the `.env` check above; verify it separately when the
  sandbox's network access matters.
- The generic CONNECT roundtrip is the same as
  [the Claude guide's](claude-proxy.md#verification).

## What the application automates

`pnr codex launch` does the whole sequence (chain check → `.env` refresh → the package-context
start with the flags and the env, restart offer for a running copy); the guard's settings sync
writes the config.toml flag **off**, the network-proxy block and the `.env` (including
`NODE_USE_ENV_PROXY`); `pnr guard wsl sync` installs the profile.d layer and the same flag and
block into the distro's `config.toml`.
