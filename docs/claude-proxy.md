# Running Claude through the proxy (Windows and WSL)

How to point every Claude component at a local forward proxy: Claude Desktop on Windows, Claude
Code on Windows, and Claude Code inside a WSL distro. These are the mechanics this application
implements; everything environment-specific (bypass zones, distro and user names, upstream
addresses) is a placeholder here — the application keeps the real values in its user config, and
so should you.

The guide uses the application's default proxy address `http://127.0.0.1:18081` — substitute your
own. The proxy itself is assumed to be a local CONNECT proxy that resolves names on its own and
dials through an upstream; what follows is the client side.

## Claude Desktop on Windows

Claude Desktop is an Electron/Chromium application: the proxy is given by Chromium command-line
flags at process start.

```text
claude-desktop.exe --proxy-server=http://127.0.0.1:18081 --proxy-bypass-list=<list>
```

The MSIX execution alias `%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe` passes the
flags through; a desktop shortcut with the flags in its target works the same way.

**The bypass list.** Entries are separated by `;`. In Chromium, `dom.com` matches only the domain
itself and `*.dom.com` matches only subdomains, so every zone that must go direct goes in **both**
spellings. The base entries that are always worth having:

```text
<local>;localhost;127.0.0.1;[::1];10.0.0.0/8;172.16.0.0/12;192.168.0.0/16;169.254.0.0/16;100.64.0.0/10;*.local
```

plus `zone;*.zone` for each bypass zone of the environment (corporate zones, internal services).
`100.64.0.0/10` is the CGNAT range — keep it bypassed when a tailscale-style overlay network is
part of the picture, or the overlay's own addresses would try to ride the proxy.

**The single-instance trap.** Claude Desktop is single-instance: a start while a copy is already
running only activates that copy's window — the flags of the new start never reach the running
processes. A copy started outside the launcher keeps its own (direct) network path until it is
closed. So: detect the running copy, close it gracefully (a window close request), force-kill what
survives the grace period, then start with the flags.

**Coverage beyond the flags.** Flags apply only to launches that carry them. The layer that takes
effect for any launch method is the managed policy: `HKLM\SOFTWARE\Policies\Claude`, string value
`egressProxyUrl` = the proxy URL (needs admin rights to set). Keep `disableWslSessions = 0` there
if the WSL integration is wanted. A missing or mismatched policy deserves a warning at launch
time — it is the safety net, not the primary path.

## Claude Code on Windows

Claude Code reads the standard proxy environment; the persistent place for it is `env` in
`%USERPROFILE%\.claude\settings.json` — it applies both to CLI sessions and to the commands run
inside them, and the engine built into the desktop app reads the same file:

```json
{
  "env": {
    "HTTP_PROXY": "http://127.0.0.1:18081",
    "HTTPS_PROXY": "http://127.0.0.1:18081",
    "NO_PROXY": "localhost,127.0.0.1,.local,corp.example.com,.corp.example.com",
    "DISABLE_TELEMETRY": "1",
    "DISABLE_ERROR_REPORTING": "1",
    "DISABLE_BUG_COMMAND": "1",
    "CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY": "1",
    "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1"
  },
  "includeCoAuthoredBy": false,
  "attribution": {
    "commit": "",
    "pr": ""
  }
}
```

`NO_PROXY` is curl-style: each zone is two entries — `zone` and `.zone` (subdomains) — unlike
Chromium's `*.zone` spelling. The same file also carries the nonessential-traffic switches
(telemetry, error reporting, the feedback paths and the session surveys off) and the attribution
keys (no `Co-Authored-By` trailer on commits, no attribution line on pull requests). Edit
surgically: preserve every other key of the file, and keep a timestamped backup before a rewrite —
the file belongs to Claude, not to the proxy setup.

## Claude Code inside WSL

Two facts shape everything:

1. **`127.0.0.1` inside a distro is the distro itself.** The Windows proxy is not reachable there.
   The distro reaches Windows through the WSL NAT gateway — the address of the `vEthernet (WSL…)`
   adapter on the Windows side. The proxy address for WSL is therefore `http://<gateway>:18081`,
   and the forward proxy must listen **on that address too**: a proxy bound to `127.0.0.1` only is
   invisible from the distro. Read the adapter address at proxy start (with retries until the
   adapter appears) and add the extra bind next to the loopback one.
2. **The NAT gateway address changes with every WSL restart.** A baked address rots silently.
   Recompute it at every shell or startup start, from inside the distro:

   ```bash
   ip route show default | awk '{print $3; exit}'
   ```

   Do not take the address from `/etc/resolv.conf`: with WSL's DNS tunneling enabled that file
   holds the tunnel stubs, not the host address.

The `~/.claude/settings.json` inside the distro has the same shape as on Windows, with the gateway
address in place of the loopback one. Because the address rots, it is not edited by hand: a
startup script at WSL start (`/etc/wsl.d/startup/…`, runs as root) refreshes it. WSL executes
nothing from that directory on its own — the wiring is the sync's job too: it points the `[boot]`
command of `/etc/wsl.conf` at a marked runner (`/etc/wsl.d/startup.sh`) that executes the startup
directory in name order; a foreign `[boot]` command is preserved as `00-user-boot-command.sh`
(runs first), and an already-done state is recognized by the runner marker and not rewritten:

```bash
#!/bin/bash
# Keeps the Claude Code proxy env pointing at the Windows side of the WSL NAT gateway,
# where the forward proxy listens. Safe to run by hand; never fails the WSL start.
USER_NAME='<user>'
USER_HOME="/home/$USER_NAME"
SETTINGS="$USER_HOME/.claude/settings.json"
PORT=18081

# Host address = default route of the WSL NAT (not resolv.conf). The adapter may appear
# later than this script, hence the retries.
HOST_IP=""
for i in 1 2 3 4 5; do
    HOST_IP=$(ip route show default 2>/dev/null | awk '{print $3; exit}')
    [ -n "$HOST_IP" ] && break
    sleep 2
done
[ -z "$HOST_IP" ] && exit 0
PROXY="http://$HOST_IP:$PORT"

# Proxy liveness does not gate the write: the Windows chain may come up later.
if timeout 3 bash -c "</dev/tcp/$HOST_IP/$PORT" 2>/dev/null; then
    echo "proxy $PROXY responds"
else
    echo "proxy $PROXY not responding yet - writing the config anyway"
fi

mkdir -p "$USER_HOME/.claude"
python3 - "$SETTINGS" "$PROXY" <<'PYEOF'
import json, os, sys
path, proxy = sys.argv[1], sys.argv[2]
data = {}
if os.path.exists(path):
    try:
        with open(path, encoding='utf-8') as f:
            data = json.load(f)
    except Exception:
        data = {}
env = data.setdefault('env', {})
env['HTTPS_PROXY'] = proxy
env['HTTP_PROXY'] = proxy
with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
PYEOF
chown "$USER_NAME:$USER_NAME" "$SETTINGS" 2>/dev/null
```

The script is idempotent and preserves the foreign keys of settings.json; `NO_PROXY`, the privacy
switches and the attribution keys are not touched by it — none of them depend on the gateway
address, so they are synced from the Windows side once and left alone.

The Windows Claude Desktop app starts its WSL host in the distro itself; the sessions it runs
there pick this env up, and that is what carries their traffic through the Windows proxy.

## Verification

Trust a live roundtrip, not "it should work". From Windows:

```text
curl.exe -s -x http://127.0.0.1:18081 -o NUL -w "%{http_code}" https://api.anthropic.com/v1/models
```

Through a working egress this answers `401` — an unauthenticated request to a public API endpoint,
which proves the API was reached; direct access from a machine where it is blocked or region-locked
answers `000` or `403` instead. The exact code is not the point; the difference between through and
around the proxy is. From WSL, the same check with the gateway address:

```bash
curl -s -x "http://$(ip route show default | awk '{print $3}'):18081" \
    -o /dev/null -w "%{http_code}" https://api.anthropic.com/v1/models
```

## Enforcement: catching what the flags miss (optional)

Flags and env cover only the launches that carry them. When direct access must be impossible,
layer the rest — each layer with stable identifiers of its own, so that re-running the setup
maintains the existing rules instead of piling up parallel ones:

- the managed `egressProxyUrl` policy — applies to any launch method;
- a hosts-file block for the service domains;
- one NRPT rule for their subtrees, pointing them at a resolver address where nothing answers —
  name resolution for them simply fails;
- an outbound firewall block by the service's address space (the provider's published ranges —
  in this application they live in the config template).

After applying or removing the block, probe all paths live: DNS, a direct connect, a proxied
connect.

## What the application automates

`pnr launch` does the desktop part (chain check → flags → restart offer for a running copy);
`pnr guard settings sync` writes the Windows settings.json (proxy env, `NO_PROXY`, the privacy
switches and the attribution keys); `pnr guard wsl sync` installs the
startup script and the distro settings.json (backups first, files touched only on an actual
change); `pnr check` is the CONNECT roundtrip.
