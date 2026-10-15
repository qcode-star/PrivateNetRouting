# Testing guide: GUI automation and the socks5-for-tailnet setup

How to exercise the application on the real machine when the session is unattended
(the workstation is usually locked), without rediscovering the tooling or the traps.
Machine-specific real values live in `.secret.agents.md`; nothing here needs them.

## Environment facts on an unattended, locked session

- **What works**: UIA patterns (`Invoke`, `Toggle`, `ExpandCollapse`, `SelectionItem`),
  `PrintWindow` captures, Win32 messages (`WM_CLOSE`, `SC_RESTORE`), the CLI, reading logs
  and process trees. The app, Explorer and the shell keep running normally.
- **What does not work**: real input — `SendInput`, cursor moves, clicks (they go to the
  secure desktop of the lock screen); UAC cannot be confirmed, so every elevated path
  (guard ban/policy, the elevated daemon stop) is out of reach and must not be triggered:
  a prompt would sit for `chain.tailscale.uacWaitSeconds` and fail. Tray-menu interaction needs a
  real click on the overflow icon — verify the tray indirectly (hide-to-tray, second-instance
  activation, balloon wiring) instead.
- **WPF first frame**: an application started on a locked session may never paint —
  `PrintWindow` returns a white image. Workaround: set `HKCU:\SOFTWARE\Microsoft\Avalon.Graphics`
  → `DisableHWAcceleration = 1` (DWORD, per-user, software rendering prints into a bitmap),
  restart the app — and **remove the key when finished**; it changes rendering for every
  WPF application of the user.
- **Shell quirks** (also in AGENTS.md): build automation scripts as `.ps1` files created
  with the file-writing tool — never bash heredocs (backslashes and quoting get mangled;
  a `\\.\pipe\...` argument written through bash arrives with eaten backslashes and "path
  not found"). Run them as `pwsh -NoProfile -File <script.ps1>`. `$pid` is a read-only
  PowerShell variable — do not use it as a parameter name.
- **Encodings**: PowerShell's `>` redirection produces UTF-16 — write dumps explicitly with
  `[System.IO.File]::WriteAllLines($path, $lines, [System.Text.UTF8Encoding]::new($false))`,
  or read them back in PowerShell, not bash. Cyrillic strings passed as command-line
  arguments from bash arrive mangled — keep them inside script files.

## Scratch tooling (keep in %TEMP%, never in the repository)

- **One window capture**: `PrintWindow` with flags `2` (`PW_RENDERFULLCONTENT` — includes
  WPF's DirectComposition content), `SetProcessDPIAware` first, rect from `GetWindowRect`
  of the process's `MainWindowHandle`. Plain `CopyFromScreen` shows the lock screen.
- **All windows of the process** (dialogs, message boxes, popups): `EnumWindows`, filter by
  process id + visible + a minimum size, and put the window rect into the output file name —
  several windows share one title (the Claude update window and its MessageBox do), and
  without it each capture overwrites the previous file.
- **UIA driver, discovery**: find the main window among `RootElement` children by process id
  **and** filter `ControlType == Window` with a non-empty `Name` — otherwise you catch a
  floating tooltip or the tray overflow flyout and wonder why `LanguageBox` is "not found".
- **UIA driver, targeting**: prefer `AutomationId` (the XAML `x:Name`) over `Name` — the
  title-bar buttons share captions with in-app controls (in the ru UI the Minimize caption is
  also the chain card's collapse chevron, and the Maximize caption is also the guards toggle).
  Buttons with compound content (a StackPanel with a dot, like the update button) have an
  **empty automation name** — find the inner text element and walk up with
  `TreeWalker.ControlViewWalker` to the Button. Combo items are `ListItem` elements whose
  name is the data item's `ToString()` — match by a substring like `Code = en,`.
- **UIA top-level enumeration is flaky for owned windows**: a modeless dialog (diagnostics,
  about, update) may vanish from `RootElement` children between queries. Do not conclude
  "the window closed itself" from a UIA walk — verify existence with `EnumWindows`.
- **Context menus** (the "?" help menu) and the tray overflow flyout are separate top-level
  windows; open the menu by invoking its button, then search all top-level windows for the
  items. The overflow flyout auto-closes when its opener disconnects.
- **Disabled buttons**: `Invoke` on one throws "unrecognized error" — and the click may still
  have landed. Treat that exception as "check the state from a fresh dump", not as "retry".
- **Synthetic-click artifact**: a chain operation started via a UIA `Invoke` is cancelled a
  few seconds after the automation client process exits — it is not reproducible when the
  client stays alive (sleep at the end of the script) and never happens from the CLI or with
  a real click. So either keep the automation session alive while an operation runs, or
  start chain operations through `pnr` and use the GUI read-only. Not an app bug; do not
  "fix" the app for it.

## What to exercise and what to watch

- Three state sources complement each other: captures (colors, layout), a UIA dump (words,
  enabled flags — the app shows a state as a word next to the color), and logs (the exact
  event chains: `chain.log`, `watchdog.log`, `watchdog-worker.log`, `launcher.log`,
  `browser.log`, `updater.log`). Flip `logging.level` to `debug` for per-request proxy
  tracing and restore `info` afterwards.
- Timers: status rows refresh every 1 s (connection counters), chain state + idle stopper
  every 10 s, guards every 60 s, the busy clock ticks every 0.5 s. Verify counters grow
  after real proxy traffic (`curl -x http://127.0.0.1:18081 ...`), and that a status row
  corrected by a poll does not stay stale.
- The full idle-stop cycle: arm it (the card toggle or any raise with `watchdog.autoStart`),
  wait `watchdog.checkSeconds`, and confirm in the logs: the WSL Claude host stop, the chain
  stop, the worker exiting by itself. The auto-rearm must fire after every raise — from the
  window, from `pnr chain up`, and from the update flow alike.
- Bring-up and cancel: the rail lights the active stage, the card's Start grays out, Stop
  stays enabled as the cancel; a cancelled bring-up extinguishes what got raised
  (the "bring-up cancelled — chain is stopped" line). The idle-stop Start button is enabled only
  over a fully up chain — the gate itself is a test case.
- Launches: `claude.exe` must carry `--proxy-server=http://127.0.0.1:18081` and the full
  `--proxy-bypass-list` (fixed local entries + both spellings of every configured zone);
  the connection evidence is the proxy counters and `forward-proxy.log`, not Claude's own
  logs (the MSIX package does not keep readable ones). Codex launches in the package
  context (`Invoke-CommandInDesktopPackage` — the only way the proxy env reaches its embedded
  CLI core); the evidence is established connections to 18081 from the `ChatGPT` processes
  plus the `codex-launcher` log line.
- Browser profile (two phases): the dialog from the "?" menu writes the profile and the
  desktop shortcut (its command line carries the same profile and, for Chrome, the egress);
  the files to check are Firefox's `user.js` (the egress from `browser.egress`: for
  `forward-proxy` — and for the http mode's chain egress — the `network.proxy.http`/`ssl`
  prefs of an HTTP CONNECT proxy, for the other `chain` egresses `network.proxy.socks`/
  `socks_port` with `socks_remote_dns`; always `failover_direct=false`,
  `browser.startup.page=3`, WebRTC off) and Chrome's `Default/Preferences`
  (`webrtc.ip_handling_policy`); Chrome's proxy and the restored session live on its
  command line (`--proxy-server=http://` or `socks5://` by the egress,
  `--restore-last-session`). Then the launch: a first click with no profile shows the
  warning + the offer; a click with a live egress starts the browser and the connections are
  visible on the egress port — 18081 for `forward-proxy`, the chain's egress port (18083 by
  default in the tailscale mode) for `chain`; a click with the chain down asks about raising
  it (not over an external endpoint — there the question is only whether to start anyway),
  and the bring-up is drawn in the chain card. The shortcut must start the browser with the
  chain up and, for the `chain` egress, the application closed. A second click reports
  "already running" without duplicating.
- The update window: with no chain it asks to raise one (a modal Yes/No — this is by
  design). While a MessageBox waits, the elapsed timer keeps ticking — that is not a hang.
  A full run against the live chain ends in "up to date" or a real offer; do not let a
  night session start the installation phase. The Codex window is the Store one: its check
  needs no chain and no UAC (the catalog is asked by product id; the version manifest only
  over a live proxy), so it stays read-only like the Claude one.
- Layout: the restored window hugs the content (`SizeToContent=Height`), the minimum width
  holds at 700, the maximized window keeps the content compact at the top (cards + launch
  actions scroll together, only the status bar is pinned), and both languages must fit
  without overlaps at MinWidth.
- Guards: `pnr guard status --live` and the card's Refresh are read-only and safe; the
  on/off buttons need UAC — skip them unattended, their fail paths are covered by unit
  tests. The settings sync needs no elevation and touches two files at once (Claude Code
  `settings.json` and Codex `config.toml`) — compare before/after with `git diff`-style
  eyes: a backup from either file must appear in the same line, and the second run must
  report "already in place". A hosts block that lags behind the configured list carries a
  visible red note at the end of the ban row (the CLI spells the same marker).
- Log rotation from the CLI honestly reports "locked by another process" while the GUI
  holds the files (FileShare.ReadWrite) — that is the designed answer, not a failure.

## socks5 mode against tailscale

The mode's contract: `chain.mode=socks5` skips every tailscale step; the forward proxy
dials `chain.socks5.upstream` (CONNECT carries the target name; the upstream resolves
it; RFC 1929 auth optional), the bring-up is a single egress check → proxy → health, and
chain down stops only the proxy. A mode change applies after an application restart.

Two working setups to get tailscale "started from the side":

1. **Recommended**: raise the app's own tailscale chain first (`pnr chain up` in the
   tailscale mode), then switch the config to socks5 with
   `chain.socks5.upstream = 127.0.0.1:18083` (the sidecar's listener) and restart the GUI. The
   app in socks5 mode never touches tailscale processes — to it the listener is just an
   external upstream. The daemon/keeper keep running as ordinary detached processes.
2. **Standalone daemon** for full independence: spawn `tailscaled --tun=userspace-networking
   --windows-mode=dev --socks5-server=127.0.0.1:<port>` with its **own** pipe and a copied
   statedir. Traps:
   - never run two tailscaled instances on one statedir — the state lock contention shows
     up as login loops, "profile not found" and a vanishing pipe;
   - a fresh statedir needs a tailnet login (interactive — impossible unattended); a copy
     of the node's statedir may lose the current-profile pointer and come up logged out;
   - the chain supervisor identifies "our" processes by the statedir/pipe substrings in the
     command line — a daemon carrying the app's statedir **will be killed** by the next
     chain up/down, and by the idle stopper's chain stop as well;
   - keep the test pipe/port strings in a `.ps1` file (see the shell quirks above).

**The ban breaks name resolution through the sidecar**: `tailscaled --socks5-server`
resolves target names through the local Windows resolver, so with the direct-access ban on
(NRPT + hosts) every banned name fails with `getaddrinfow: no data of the requested type`.
The tailscale mode is immune (DoH first, dial by IP); the socks5 mode against the sidecar
is not. A real external upstream resolves on its own side and is unaffected — the
limitation belongs to this test setup, not to the mode. For a green-path test set
`forwardProxy.healthCheckHost` to a non-banned HTTPS host for the run and restore it (the
health host drives both the egress check and the CONNECT health probe).

Socks5-mode checklist: the rail has three stages (Egress / Forward proxy / CONNECT
check), the upstream row shows the address with its reachability word, `pnr chain status`
prints the `upstream:` line, real traffic passes through 18081, the log carries
`upstream resolves` and no DoH activity, and chain down stops only the proxy.

Cleanup discipline: restore `chain.mode`, `logging.level` and `healthCheckHost`; kill stray
daemons matching the test pipe; remove copied statedirs; re-check `pnr chain status` in the
tailscale mode afterwards.

## Deploy/test cycle and hygiene

- Build + tests from the repository, then publish **both** projects self-contained into the
  install directory — the running exe locks the folder, so stop the application first and
  start it again after (hard kill is the sanctioned path).
- `pnr chain up` and the other CLI commands that leave a client behind hand the in-process
  proxy over to a detached worker before they exit, so the chain stays whole afterwards;
  `forwardProxy.detached` hosts the proxy in a worker from the start.
- Before every commit check the **staged** diff (`git diff --cached`) — not just `git diff`
  — and the untracked list: `git add -A` sweeps whatever the test scripts created in the
  working tree (daemon logs, for one, carry machine-specific data).
  Keep scratch scripts and captures in `%TEMP%`.
