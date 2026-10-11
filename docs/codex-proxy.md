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
```

Keep every foreign line of the file intact and rewrite only the four proxy lines. Refresh the file
before every launch: a WSL codex session rewrites it to the WSL gateway address (below), which is
unreachable from Windows once WSL is down — with a stale address the core would silently stop
reaching anything.

## config.toml: respect_system_proxy

`%USERPROFILE%\.codex\config.toml` (owned by the app itself; a `[features]` section is typically
absent until added):

```toml
[features]
respect_system_proxy = true
```

The flag makes Codex honour the host's system proxy (WinINET), so it works together with a
system-proxy layer; with the system proxy off it is inert belt-and-braces. The app rewrites the
file on its own, so the edit must be surgical: every other line survives byte for byte, the
newline style is kept, the key is inserted into an existing `[features]` section (or one is
appended), and the top-level dotted spelling `features.respect_system_proxy = true` is an
equivalent form.

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

One shared `.env` serving two worlds with different address semantics is fragile, so the WSL
startup script keeps a second line of defence: a small python3 relay that listens on
`127.0.0.1:<port>` inside the distro and forwards raw TCP to the Windows-side proxy on the NAT
gateway (the gateway address is read at WSL start and is stable for the WSL instance's lifetime).
With the relay up, the loopback form the Windows side keeps in the shared `.env` is valid inside
the distro too — the rewrite above becomes an optimisation rather than a requirement, and the
scheme survives a core started outside a login shell, where the snippet never runs. The relay
exits after ten idle minutes so it does not keep the WSL VM alive on its own; the startup script
restarts it at the next WSL boot, and the profile snippet revives it on demand.

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
- The generic CONNECT roundtrip is the same as
  [the Claude guide's](claude-proxy.md#verification).

## What the application automates

`pnr codex launch` does the whole sequence (chain check → `.env` refresh → the package-context
start with the flags and the env, restart offer for a running copy); the guard's settings sync
writes the config.toml flag and the `.env`; `pnr guard wsl sync` installs the profile.d layer.
