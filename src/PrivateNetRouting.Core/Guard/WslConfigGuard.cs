using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Net;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Core.Guard;

/// <summary>State of the boot runner file (/etc/wsl.d/startup.sh by default) inside a distro.</summary>
internal enum WslBootRunnerState
{
    Missing,
    Ours,
    Foreign,
}

/// <summary>What the boot-layer step has to do for one distro.</summary>
internal enum WslBootPlan
{
    /// <summary>The runner is in place and marked, wsl.conf already points at it — nothing to do.</summary>
    AlreadyOurs,

    /// <summary>No usable [boot] command — write the runner and point the command at it.</summary>
    InstallRunner,

    /// <summary>
    /// The command already points at the runner path but the file is not ours (a foreign runner,
    /// e.g. the wrapper of the pre-application scheme) — replace the file, keep the command:
    /// wrapping a runner of the same directory into that directory would recurse.
    /// </summary>
    TakeoverRunner,

    /// <summary>A foreign command occupies the slot — preserve it as the first script of the startup directory and point wsl.conf at the runner.</summary>
    AdoptForeignCommand,
}

/// <summary>Status of one distro from wsl.distros.</summary>
public sealed record WslDistroStatus(
    string Distro,
    bool Sync,
    GuardState State,
    int? ScriptPort,
    string? CurrentProxy,
    string Reason,
    bool ProxyEnvPresent = false,
    int? ProxyEnvPort = null);

/// <summary>Status of the WSL layer: off (Reason explains) or an aggregate over distros. Warning — a read-only note about the environment (e.g. a loopback-only applications proxy endpoint the distros cannot reach).</summary>
public sealed record WslConfigStatus(
    GuardState State,
    IReadOnlyList<WslDistroStatus> Distros,
    int ExpectedPort,
    string? ExpectedProxy,
    string Reason,
    string? Warning = null)
{
    public static WslConfigStatus LayerOff(string reason) => new(GuardState.Off, [], 0, null, reason);
}

public sealed record WslDistroSyncResult(
    string Distro,
    bool Skipped,
    string? Reason,
    string? ProxyUrl,
    bool SettingsWritten,
    string? SettingsBackup,
    bool ScriptWritten,
    string? ScriptBackup,
    bool ProxyEnvWritten = false,
    string? ProxyEnvBackup = null,
    bool BootRunnerWritten = false,
    bool UserCommandWritten = false,
    bool WslConfWritten = false,
    IReadOnlyList<string>? BootBackups = null,
    bool Failed = false);

public sealed record WslSyncResult(bool LayerDisabled, string? Reason, IReadOnlyList<WslDistroSyncResult> Distros);

/// <summary>
/// WSL layer of the guard. The layer is entirely optional: wsl.mode = on | off | auto
/// (auto — on only if WSL is present and at least one configured distro is installed); with
/// both wsl.syncClaude and wsl.syncCodex off there is nothing to maintain — the layer reports off.
/// The Claude layer (wsl.syncClaude, every configured distro): the startup script
/// (proxy address = IP of the Windows side of WSL NAT + application port) and the full env in
/// ~/.claude/settings.json (proxy + NO_PROXY). A file is rewritten only when its content actually
/// changes; before a rewrite the previous version is preserved unless an identical copy is already
/// among the `.pnr-bak-*` backups (the matching copy is touched instead, so mtime order mirrors the
/// states most recently preserved); the copies live in three numbered slots, the one preserved
/// longest ago is reused (the legacy `.bak-<timestamp>` naming is cleaned up on every sync).
/// Status is read-only and only in running distros (they are not started just for a
/// check); sync does not start a distro — it skips it with a reason.
///
/// The same sync writes the Codex proxy env layer (wsl.proxyEnvPath, gated by wsl.syncCodex):
/// the Codex desktop app runs codex *inside* the distro
/// (wsl.exe -d … --exec /usr/bin/bash -lc …), where 127.0.0.1 is the distro itself and none of
/// the Windows proxies exist. The snippet is a profile.d file, so it is read by every login
/// shell, but it exports the proxy only to the shells Codex itself starts — the desktop app
/// passes CODEX_HOME into the distro for its processes, and the codex command name is the
/// fallback; ordinary terminals, apt, pip and git keep the direct route.
///
/// The same sync wires the boot layer of every synced distro: the [boot] command of
/// /etc/wsl.conf is pointed at a marked runner (/etc/wsl.d/startup.sh, see BuildBootRunner)
/// that executes every script of the startup directory in name order. A foreign command
/// occupying the slot is preserved as 00-user-boot-command.sh (runs first); a foreign file
/// already sitting at the runner path is replaced — wrapping it into the directory it serves
/// would recurse. The state is recognized by the runner marker: an already-done distro gets
/// no new wiring (PlanBootLayer), but the runner file itself is still kept current.
/// </summary>
public sealed class WslConfigGuard(
    AppConfig config,
    IWslProcessRunner wsl,
    Func<System.Net.IPAddress?>? gatewayProvider = null)
{
    private readonly Func<System.Net.IPAddress?> _gateway = gatewayProvider ?? WslGateway.FindIpv4;

    public async Task<WslConfigStatus> ReadAsync(CancellationToken ct)
    {
        var (enabled, reason) = await EvaluateLayerAsync(ct);
        if (!enabled)
        {
            return WslConfigStatus.LayerOff(reason);
        }

        var expectedPort = ClaudeEnvValues.Port(config);
        var expectedProxy = ClaudeEnvValues.WslProxyUrl(_gateway(), config);
        var running = await ListDistrosAsync("--running", ct);
        var installed = await ListDistrosAsync("--quiet", ct);

        var distros = new List<WslDistroStatus>();
        foreach (var distro in ConfiguredDistros())
        {
            distros.Add(await ReadDistroAsync(distro, running, installed, expectedPort, expectedProxy, ct));
        }

        return new WslConfigStatus(Aggregate(distros), distros, expectedPort, expectedProxy, "", ReachabilityWarning(config));
    }

    /// <summary>
    /// Read-only note for the status (no firewall or listen changes on our own — rule #6): with
    /// the local proxy skipped the applications endpoint is the upstream's inbound, and a
    /// loopback-only one is unreachable from the distros — the synced proxy addresses would point
    /// at nothing. The upstream has to listen on the WSL-facing address (and Windows firewall
    /// must allow it); the exact listen setup is the owner's side.
    /// </summary>
    internal static string? ReachabilityWarning(AppConfig config)
    {
        if (!AppProxyEndpoint.IsSkipped(config))
        {
            return null;
        }

        var endpoint = AppProxyEndpoint.EndPoint(config);
        return !System.Net.IPAddress.IsLoopback(endpoint.Address)
            ? null
            : $"the applications proxy endpoint {endpoint} is loopback-only — unreachable from the distros; " +
              "the upstream must listen on the WSL-facing address (and Windows firewall must allow it)";
    }

    public async Task<WslSyncResult> SyncAsync(CancellationToken ct)
    {
        var (enabled, reason) = await EvaluateLayerAsync(ct);
        if (!enabled)
        {
            return new WslSyncResult(true, reason, []);
        }

        var running = await ListDistrosAsync("--running", ct);
        var installed = await ListDistrosAsync("--quiet", ct);
        var gateway = _gateway();
        var results = new List<WslDistroSyncResult>();
        foreach (var distro in ConfiguredDistros())
        {
            if (!installed.Contains(distro.Name, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new WslDistroSyncResult(distro.Name, true, "distro is not installed", null, false, null, false, null));
                continue;
            }

            if (!running.Contains(distro.Name, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new WslDistroSyncResult(distro.Name, true, "distro is not running", null, false, null, false, null));
                continue;
            }

            // one broken distro must not abort the others: its failure becomes its own result
            // (reported through the same skipped-with-reason surface); cancellation still propagates
            try
            {
                results.Add(await SyncDistroAsync(distro, gateway, ct));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException
                or System.ComponentModel.Win32Exception)
            {
                results.Add(new WslDistroSyncResult(distro.Name, true, $"sync failed: {ex.Message}", null, false, null, false, null, Failed: true));
            }
        }

        return new WslSyncResult(false, null, results);
    }

    /// <summary>Distros with the required fields; the config order is preserved.</summary>
    internal IReadOnlyList<WslDistroConfig> ConfiguredDistros() =>
        config.Wsl.Distros
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.User))
            .ToList();

    /// <summary>
    /// Layer on/off: off — always off; an empty list of configured distros — off; both sync
    /// flags off — nothing to maintain, off; on — on; auto — on only if WSL responds and at
    /// least one configured distro is installed (running wsl with no distros prints a message
    /// instead of names — no name will match, output localization does not matter).
    /// </summary>
    internal async Task<(bool Enabled, string Reason)> EvaluateLayerAsync(CancellationToken ct)
    {
        var mode = config.Wsl.Mode.Trim().ToLowerInvariant();
        var configured = ConfiguredDistros();
        if (mode == "off")
        {
            return (false, "wsl layer is disabled (wsl.mode=off)");
        }

        if (!config.Wsl.SyncClaude && !config.Wsl.SyncCodex)
        {
            return (false, "nothing to sync (wsl.syncClaude and wsl.syncCodex are off)");
        }

        if (configured.Count == 0)
        {
            return (false, "no distros configured (wsl.distros)");
        }

        if (mode == "on")
        {
            return (true, "");
        }

        try
        {
            var installed = await ListDistrosAsync("--quiet", ct);
            return configured.Any(d => installed.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                ? (true, "")
                : (false, "auto: none of the configured distros is installed");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return (false, $"auto: WSL is not available ({ex.Message})");
        }
    }

    private async Task<WslDistroStatus> ReadDistroAsync(
        WslDistroConfig distro,
        IReadOnlyList<string> running,
        IReadOnlyList<string> installed,
        int expectedPort,
        string? expectedProxy,
        CancellationToken ct)
    {
        if (!installed.Contains(distro.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new WslDistroStatus(distro.Name, config.Wsl.SyncClaude, GuardState.Unknown, null, null, "distro is not installed");
        }

        if (!running.Contains(distro.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new WslDistroStatus(distro.Name, config.Wsl.SyncClaude, GuardState.Unknown, null, null, "distro is not running");
        }

        try
        {
            var read = await ReadInsideAsync(distro, ct);
            if (config.Wsl.SyncClaude && !read.ScriptPresent)
            {
                return new WslDistroStatus(distro.Name, config.Wsl.SyncClaude, GuardState.Off, null, read.SettingsProxy, "startup script not found");
            }

            // a layer that is not synced cannot be stale: its signals are ignored
            var expectedHost = ClaudeEnvValues.WslFixedHost(config);
            var portOk = !config.Wsl.SyncClaude || (read.ScriptPort == expectedPort && read.ScriptHost == expectedHost);
            var proxyOk = !config.Wsl.SyncClaude || expectedProxy is null || read.SettingsProxy == expectedProxy;
            var envOk = !config.Wsl.SyncCodex
                || (read.ProxyEnvPresent && read.ProxyEnvPort == expectedPort && read.ProxyEnvHost == expectedHost);
            var ok = portOk && proxyOk && envOk;
            return new WslDistroStatus(
                distro.Name,
                config.Wsl.SyncClaude,
                ok ? GuardState.On : GuardState.Partial,
                read.ScriptPort,
                read.SettingsProxy,
                ok ? "" : DescribeMismatch(read, expectedPort, expectedHost, expectedProxy, config.Wsl.SyncCodex),
                read.ProxyEnvPresent,
                read.ProxyEnvPort);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            return new WslDistroStatus(distro.Name, config.Wsl.SyncClaude, GuardState.Unknown, null, null, $"failed to read: {ex.Message}");
        }
    }

    /// <summary>
    /// The bash helper embedded in every sync script (each script is a separate bash pass):
    /// preserves a file in `<file>.pnr-bak-<n>` unless an identical copy already exists — then
    /// just touches it (mtime order mirrors the states most recently preserved; fresh copies are
    /// touched too, cp -p keeps the source mtime). Three numbered slots; the one preserved longest
    /// ago is reused when all are taken. Emits `<prefix>=<path>` only for a fresh copy, so a
    /// deduplicated backup simply produces no BACKUP line.
    /// </summary>
    internal const string PnrBakFn = """
        pnr_bak() {
            # pnr_bak <file> <prefix>: copy <file> to <file>.pnr-bak-<n> unless an identical
            # .pnr-bak- copy already exists — a state is preserved only once (then just touched,
            # so mtime order mirrors the recently preserved states).
            local f="$1" prefix="$2" b n slot
            for b in "$f".pnr-bak-*; do
                [ -f "$b" ] || continue
                if cmp -s "$f" "$b"; then
                    touch "$b"
                    return 0
                fi
            done
            for n in 1 2 3; do
                if [ ! -e "$f.pnr-bak-$n" ]; then
                    slot="$f.pnr-bak-$n"
                    break
                fi
            done
            if [ -z "$slot" ]; then
                slot=$(ls -1t "$f".pnr-bak-[123] 2>/dev/null | tail -n 1)
            fi
            cp -p "$f" "$slot" && touch "$slot" && echo "$prefix=$slot"
        }
        pnr_put() {
            # pnr_put <new> <dest>: replaces <dest> with the payload file. The payload must be
            # non-empty (a failed decode or a full /tmp leaves an empty one), and the write goes
            # through a temp file next to <dest>, so a failure never leaves a truncated file
            # behind; mode and owner of an existing file are kept, a symlink is followed.
            local new="$1" dest tmp
            dest=$(readlink -f "$2" 2>/dev/null)
            [ -n "$dest" ] || dest="$2"
            [ -s "$new" ] || { echo "FAILED: empty payload for $2"; return 1; }
            tmp="$dest.pnr-tmp"
            cat "$new" > "$tmp" || { rm -f "$tmp"; echo "FAILED: cannot write $tmp"; return 1; }
            if [ -f "$dest" ]; then
                chmod --reference="$dest" "$tmp" 2>/dev/null
                chown --reference="$dest" "$tmp" 2>/dev/null
            fi
            mv -f "$tmp" "$dest" || { rm -f "$tmp"; echo "FAILED: cannot replace $dest"; return 1; }
        }
        """;

    private async Task<WslDistroSyncResult> SyncDistroAsync(WslDistroConfig distro, System.Net.IPAddress? gateway, CancellationToken ct)
    {
        var proxy = ClaudeEnvValues.WslProxyUrl(gateway, config)
            ?? throw new IOException($"WSL NAT gateway (vEthernet) not found for '{distro.Name}' — is WSL up?");

        if (config.Wsl.SyncClaude && !IsDedicatedStartupScriptPath(config.Wsl.StartupScriptPath))
        {
            throw new IOException(
                $"wsl.startupScriptPath '{config.Wsl.StartupScriptPath}' must live in a dedicated directory named 'startup' "
                + "(e.g. /etc/wsl.d/startup/10-claude-proxy.sh): the boot runner executes every script of that directory as root");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var read = await ReadInsideAsync(distro, ct);

        // The Claude layer (wsl.syncClaude): the boot layer (the [boot] command of /etc/wsl.conf →
        // the marked runner that executes the startup directory), the full env in
        // ~/.claude/settings.json and the startup script refreshing the proxy address on every
        // distro start. Off — none of the passes run, nothing of the Claude side is touched.
        var scriptPath = config.Wsl.StartupScriptPath;
        var runnerPath = BootRunnerPath(scriptPath);
        var bootCommand = ExtractBootCommand(read.WslConfText);
        var bootPlan = PlanBootLayer(runnerPath, bootCommand, read.BootRunner);
        var bootRunnerWritten = false;
        var userCommandWritten = false;
        var wslConfWritten = false;
        List<string> bootBackups = [];
        var settingsOutput = "";
        var scriptOutput = "";
        if (config.Wsl.SyncClaude)
        {
            // the boot pass always ensures the runner file itself (cmp makes it a no-op when
            // current); the plan decides only about the preserved user command and the wsl.conf edit
            var bootScript = BuildBootSyncScript(
                bootPlan, runnerPath, UserCommandPath(scriptPath), StartupDir(scriptPath),
                bootCommand, SetBootCommand(read.WslConfText, runnerPath), stamp);
            var bootOutput = await wsl.RunAsync(["-d", distro.Name, "-u", "root", "--", "bash", "-s"], bootScript, Encoding.UTF8, ct);
            RequireCompleted(bootOutput, $"boot layer sync in '{distro.Name}'");
            bootRunnerWritten = bootOutput.Contains("RUNNER=written", StringComparison.Ordinal);
            userCommandWritten = bootOutput.Contains("USERCMD=written", StringComparison.Ordinal);
            wslConfWritten = bootOutput.Contains("WSLCONF=written", StringComparison.Ordinal);
            bootBackups = bootOutput.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("BACKUP_", StringComparison.Ordinal))
                .Select(l => l[(l.IndexOf('=') + 1)..].Trim())
                .ToList();

            // distro settings.json: full env (gateway proxy + NO_PROXY), foreign keys are preserved;
            // the backup is made only when the content actually changes
            var root = read.SettingsRoot ?? [];
            var env = root["env"] as JsonObject ?? [];
            env["HTTPS_PROXY"] = proxy;
            env["HTTP_PROXY"] = proxy;
            env["NO_PROXY"] = ClaudeEnvValues.BuildNoProxy(config.BypassDomains);
            root["env"] = env;
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            var settingsScript = NormalizeLf($$"""
                {{PnrBakFn}}
                SETTINGS="$HOME/.claude/settings.json"
                mkdir -p "$HOME/.claude" || { echo "FAILED: cannot create $HOME/.claude"; exit 1; }
                NEW="/tmp/.pnr-settings-{{stamp}}"
                echo '{{DistroPayload(json)}}' | base64 -d > "$NEW" || { echo "FAILED: cannot write the payload to /tmp"; exit 1; }
                if [ -f "$SETTINGS" ] && cmp -s "$NEW" "$SETTINGS"; then
                    echo SETTINGS=unchanged
                else
                    if [ -f "$SETTINGS" ]; then pnr_bak "$SETTINGS" BACKUP || { echo "FAILED: backup of $SETTINGS"; exit 1; }; fi
                    pnr_put "$NEW" "$SETTINGS" || exit 1
                    echo SETTINGS=written
                fi
                rm -f "$NEW"
                ls -1t "$HOME"/.claude/settings.json.pnr-bak-* 2>/dev/null | tail -n +4 | xargs -r rm -f
                find "$HOME/.claude" -maxdepth 1 -name 'settings.json.bak-2[0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]' -type f -delete
                echo PNR=ok
                """);
            settingsOutput = await wsl.RunAsync(["-d", distro.Name, "-u", distro.User, "--", "bash", "-s"], settingsScript, Encoding.UTF8, ct);
            RequireCompleted(settingsOutput, $"settings.json sync in '{distro.Name}'");

            // startup script: refreshes the proxy address on every distro start
            var scriptContent = BuildStartupScript(distro.User, ClaudeEnvValues.Port(config), ClaudeEnvValues.WslFixedHost(config));
            var rootScript = NormalizeLf($$"""
                {{PnrBakFn}}
                SCRIPT="{{scriptPath}}"
                NEW="/tmp/.pnr-startup-{{stamp}}"
                echo '{{DistroPayload(scriptContent)}}' | base64 -d > "$NEW" || { echo "FAILED: cannot write the payload to /tmp"; exit 1; }
                mkdir -p "$(dirname "$SCRIPT")"
                if [ -f "$SCRIPT" ] && cmp -s "$NEW" "$SCRIPT"; then
                    echo SCRIPT=unchanged
                else
                    if [ -f "$SCRIPT" ]; then pnr_bak "$SCRIPT" BACKUP || { echo "FAILED: backup of $SCRIPT"; exit 1; }; fi
                    pnr_put "$NEW" "$SCRIPT" || exit 1
                    chmod 755 "$SCRIPT"
                    echo SCRIPT=written
                fi
                rm -f "$NEW"
                ls -1t {{scriptPath}}.pnr-bak-* 2>/dev/null | tail -n +4 | xargs -r rm -f
                find "$(dirname "{{scriptPath}}")" -maxdepth 1 -name '{{Path.GetFileName(scriptPath)}}.bak-2[0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]' -type f -delete
                echo PNR=ok
                """);
            scriptOutput = await wsl.RunAsync(["-d", distro.Name, "-u", "root", "--", "bash", "-s"], rootScript, Encoding.UTF8, ct);
            RequireCompleted(scriptOutput, $"startup script sync in '{distro.Name}'");
        }

        // Codex proxy env: a profile.d snippet exporting the proxy to the shells Codex itself
        // starts inside the distro (see the class comment). wsl.syncCodex=false leaves the
        // file alone — the layer is then reported as off for this distro, not as a mismatch.
        var envPath = config.Wsl.ProxyEnvPath;
        var envWritten = false;
        string? envBackup = null;
        if (config.Wsl.SyncCodex && !string.IsNullOrWhiteSpace(envPath))
        {
            var envContent = BuildCodexProxyEnvScript(
                ClaudeEnvValues.Port(config),
                ClaudeEnvValues.BuildNoProxy(config.BypassDomains),
                ClaudeEnvValues.WslFixedHost(config));
            var envScript = NormalizeLf($$"""
                {{PnrBakFn}}
                ENVFILE="{{envPath}}"
                NEW="/tmp/.pnr-codexenv-{{stamp}}"
                echo '{{DistroPayload(envContent)}}' | base64 -d > "$NEW" || { echo "FAILED: cannot write the payload to /tmp"; exit 1; }
                mkdir -p "$(dirname "$ENVFILE")"
                if [ -f "$ENVFILE" ] && cmp -s "$NEW" "$ENVFILE"; then
                    echo ENVFILE=unchanged
                else
                    if [ -f "$ENVFILE" ]; then pnr_bak "$ENVFILE" BACKUP || { echo "FAILED: backup of $ENVFILE"; exit 1; }; fi
                    pnr_put "$NEW" "$ENVFILE" || exit 1
                    chmod 644 "$ENVFILE"
                    echo ENVFILE=written
                fi
                rm -f "$NEW"
                ls -1t {{envPath}}.pnr-bak-* 2>/dev/null | tail -n +4 | xargs -r rm -f
                echo PNR=ok
                """);
            var envOutput = await wsl.RunAsync(["-d", distro.Name, "-u", "root", "--", "bash", "-s"], envScript, Encoding.UTF8, ct);
            RequireCompleted(envOutput, $"codex proxy env sync in '{distro.Name}'");
            envWritten = envOutput.Contains("ENVFILE=written", StringComparison.Ordinal);
            envBackup = envWritten ? FindBackupName(envOutput) : null;
        }

        var settingsWritten = settingsOutput.Contains("SETTINGS=written", StringComparison.Ordinal);
        var scriptWritten = scriptOutput.Contains("SCRIPT=written", StringComparison.Ordinal);
        return new WslDistroSyncResult(
            distro.Name,
            Skipped: false, Reason: null, proxy,
            SettingsWritten: settingsWritten,
            SettingsBackup: settingsWritten ? FindBackupName(settingsOutput) : null,
            ScriptWritten: scriptWritten,
            ScriptBackup: scriptWritten ? FindBackupName(scriptOutput) : null,
            ProxyEnvWritten: envWritten,
            ProxyEnvBackup: envBackup,
            BootRunnerWritten: bootRunnerWritten,
            UserCommandWritten: userCommandWritten,
            WslConfWritten: wslConfWritten,
            BootBackups: bootBackups);
    }

    /// <summary>
    /// Everything this guard sends into a distro rides bash — piped to `bash -s` or embedded as
    /// a base64 payload written to a file — and a single CR breaks it (a syntax error on the
    /// first line, a "bad interpreter", a payload that never matches its file again) while
    /// wsl.exe still exits 0. Raw multiline string literals take the line endings of the source
    /// checkout, so every script and payload is normalized to LF at the boundary: the checkout's
    /// line endings must not reach the distros.
    /// </summary>
    internal static string NormalizeLf(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>The base64 payload of a file destined for inside a distro, LF-normalized.</summary>
    internal static string DistroPayload(string content) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(NormalizeLf(content)));

    /// <summary>
    /// bash failures inside the distro are invisible to wsl.exe exit codes (a CRLF checkout once
    /// killed every script on the first line with a syntax error while the sync reported success),
    /// so the scripts end with a marker line and its absence is an error carrying the output.
    /// </summary>
    private static void RequireCompleted(string output, string what)
    {
        if (output.Contains("PNR=ok", StringComparison.Ordinal))
        {
            return;
        }

        var detail = string.Join(" | ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        if (detail.Length > 400)
        {
            detail = detail[..400] + "...";
        }

        throw new IOException($"{what}: the script did not run to the end: {detail}");
    }

    private static string? FindBackupName(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("BACKUP=", StringComparison.Ordinal)) is { } line
            ? line["BACKUP=".Length..].Trim()
            : null;

    private async Task<IReadOnlyList<string>> ListDistrosAsync(string listFlag, CancellationToken ct)
    {
        var listing = await wsl.RunAsync(["--list", listFlag, "--quiet"], null, Encoding.Unicode, ct);
        return listing.Split('\n')
            .Select(line => line.Replace("\0", "").Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    internal sealed record WslRead(
        bool ScriptPresent,
        int? ScriptPort,
        string? ScriptUser,
        bool SettingsPresent,
        string? SettingsProxy,
        JsonObject? SettingsRoot,
        bool ProxyEnvPresent = false,
        int? ProxyEnvPort = null,
        string? WslConfText = null,
        WslBootRunnerState BootRunner = WslBootRunnerState.Missing,
        string? ScriptHost = null,
        string? ProxyEnvHost = null);

    /// <summary>One trip into the distro for all state (root: sees both /etc and home).</summary>
    private async Task<WslRead> ReadInsideAsync(WslDistroConfig distro, CancellationToken ct)
    {
        var scriptPath = config.Wsl.StartupScriptPath;
        var envPath = config.Wsl.ProxyEnvPath;
        // the user's real home (root, or one outside /home), not /home/<user>: the sync writes
        // through $HOME as that user, so the read has to look at the same file
        const string settingsPath = "\"$PNR_HOME/.claude/settings.json\"";
        var runnerPath = BootRunnerPath(scriptPath);

        // An empty path would make `[ -f  ]` true and `grep -E …` with no file read the rest of
        // the probe script from stdin, so the block exists only for a configured path.
        var envProbe = string.IsNullOrWhiteSpace(envPath)
            ? "echo PROXYENV=missing"
            : $"""
            if [ -f {envPath} ]; then
                echo PROXYENV=present
                grep -E '^PNR_CODEX_PROXY_PORT=' {envPath} 2>/dev/null || true
                grep -E '^PNR_CODEX_PROXY_HOST=' {envPath} 2>/dev/null || true
            else
                echo PROXYENV=missing
            fi
            """;
        var probe = NormalizeLf($"""
            PNR_HOME=$(getent passwd '{distro.User}' | cut -d: -f6)
            [ -n "$PNR_HOME" ] || PNR_HOME="/home/{distro.User}"
            if [ -f {scriptPath} ]; then
                echo SCRIPT=present
                grep -E '^PORT=' {scriptPath} 2>/dev/null || true
                grep -E '^PNR_PROXY_HOST=' {scriptPath} 2>/dev/null || true
                grep -E '^USER_NAME=' {scriptPath} 2>/dev/null || true
            else
                echo SCRIPT=missing
            fi
            {envProbe}
            if [ -f {runnerPath} ]; then
                if grep -q '{BootRunnerMarker}' {runnerPath} 2>/dev/null; then
                    echo RUNNER=ours
                else
                    echo RUNNER=foreign
                fi
            else
                echo RUNNER=missing
            fi
            if [ -f /etc/wsl.conf ]; then
                echo WSLCONF=present
                cat /etc/wsl.conf
                echo
                echo WSLCONF=END
            else
                echo WSLCONF=missing
            fi
            if [ -f {settingsPath} ]; then
                echo SETTINGS=present
                cat {settingsPath}
                echo
                echo SETTINGS=END
            else
                echo SETTINGS=missing
            fi
            echo PNR=ok
            """);
        var output = await wsl.RunAsync(["-d", distro.Name, "-u", "root", "--", "bash", "-s"], probe, Encoding.UTF8, ct);
        RequireCompleted(output, $"state read of '{distro.Name}'");
        return ParseRead(output);
    }

    internal static WslRead ParseRead(string output)
    {
        bool scriptPresent = false, settingsFilePresent = false, proxyEnvPresent = false;
        int? port = null, proxyEnvPort = null;
        string? user = null, proxy = null, wslConf = null, scriptHost = null, proxyEnvHost = null;
        var bootRunner = WslBootRunnerState.Missing;
        JsonObject? root = null;

        var lines = output.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            switch (line)
            {
                case "SCRIPT=present":
                    scriptPresent = true;
                    break;
                case "SCRIPT=missing":
                    scriptPresent = false;
                    break;
                case "PROXYENV=present":
                    proxyEnvPresent = true;
                    break;
                case "PROXYENV=missing":
                    proxyEnvPresent = false;
                    break;
                case "RUNNER=ours":
                    bootRunner = WslBootRunnerState.Ours;
                    break;
                case "RUNNER=foreign":
                    bootRunner = WslBootRunnerState.Foreign;
                    break;
                case "RUNNER=missing":
                    bootRunner = WslBootRunnerState.Missing;
                    break;
                case "WSLCONF=present":
                    // wsl.conf body — up to the WSLCONF=END line (or the end of the output)
                    var confEnd = Array.IndexOf(lines, "WSLCONF=END", i + 1);
                    wslConf = string.Join('\n', lines[(i + 1)..(confEnd > 0 ? confEnd : lines.Length)]);
                    i = confEnd > 0 ? confEnd : lines.Length;
                    break;
                case "WSLCONF=missing":
                    wslConf = null;
                    break;
                case "SETTINGS=present":
                    settingsFilePresent = true;
                    // settings.json body — up to the SETTINGS=END line (or the end of the output)
                    var end = Array.IndexOf(lines, "SETTINGS=END", i + 1);
                    var jsonText = string.Join('\n', lines[(i + 1)..(end > 0 ? end : lines.Length)]).Trim();
                    if (jsonText.Length > 0)
                    {
                        root = JsonNode.Parse(jsonText) as JsonObject;
                        if (root?["env"]?["HTTPS_PROXY"] is { } node)
                        {
                            proxy = node.GetValue<string>();
                        }
                    }

                    i = end > 0 ? end : lines.Length;
                    break;
                case "SETTINGS=missing":
                    settingsFilePresent = false;
                    break;
                default:
                    if (line.StartsWith("PORT=", StringComparison.Ordinal)
                        && int.TryParse(line["PORT=".Length..], out var parsed))
                    {
                        port = parsed;
                    }
                    else if (line.StartsWith("PNR_CODEX_PROXY_PORT=", StringComparison.Ordinal)
                             && int.TryParse(line["PNR_CODEX_PROXY_PORT=".Length..], out var envPort))
                    {
                        proxyEnvPort = envPort;
                    }
                    else if (line.StartsWith("PNR_CODEX_PROXY_HOST=", StringComparison.Ordinal))
                    {
                        proxyEnvHost = line["PNR_CODEX_PROXY_HOST=".Length..].Trim().Trim('\'', '"');
                    }
                    else if (line.StartsWith("PNR_PROXY_HOST=", StringComparison.Ordinal))
                    {
                        scriptHost = line["PNR_PROXY_HOST=".Length..].Trim().Trim('\'', '"');
                    }
                    else if (line.StartsWith("USER_NAME=", StringComparison.Ordinal))
                    {
                        user = line["USER_NAME=".Length..].Trim().Trim('\'', '"');
                    }

                    break;
            }
        }

        return new WslRead(scriptPresent, port, user, settingsFilePresent, proxy, root, proxyEnvPresent, proxyEnvPort, wslConf, bootRunner, scriptHost, proxyEnvHost);
    }

    /// <summary>
    /// The Codex proxy env layer: a profile snippet that exports the application proxy to the
    /// shells Codex itself starts inside the distro and to nothing else. Markers: the desktop app
    /// always passes CODEX_HOME into the distro for its own processes, and the codex command name
    /// catches a launch started by hand from such a shell. Ordinary shells, apt, pip and git keep
    /// the direct route. The host address is recomputed at every shell start: 127.0.0.1 inside the
    /// distro is the distro itself, and the WSL NAT address changes with every WSL restart, so a
    /// baked address would rot. The port is baked at sync time (PNR_CODEX_PROXY_PORT — the line
    /// `pnr guard status` reads back). Besides exporting the variables, the snippet rewrites
    /// "$CODEX_HOME/.env" to the gateway address when it exists: the Codex core loads that file
    /// over the inherited environment (see CodexEnvFile), so the exports alone would be overridden
    /// by the loopback form the Windows side keeps there. The Windows launcher refreshes the file
    /// back before every launch.
    /// </summary>
    internal static string BuildCodexProxyEnvScript(int port, string noProxy, string? fixedHost = null) => NormalizeLf($$"""
        # Managed by PrivateNetRouting (guard.wsl.proxyEnvPath) — do not edit by hand.
        # Exports the application's forward proxy to the Codex CLI running inside this distro:
        # the desktop app passes CODEX_HOME into the distro for its own processes (the marker),
        # the codex command name is the fallback. Everything else — ordinary shells, apt, pip,
        # git — keeps the direct route.
        PNR_CODEX_PROXY_PORT={{port}}
        {{(fixedHost is null ? "" : $"PNR_CODEX_PROXY_HOST='{fixedHost}'")}}
        _pnr_codex_shell=0
        if [ -n "${CODEX_HOME:-}" ]; then
            _pnr_codex_shell=1
        fi
        case "${BASH_EXECUTION_STRING:-}" in
            *codex*|*Codex*) _pnr_codex_shell=1 ;;
        esac
        if [ "$_pnr_codex_shell" = "1" ]; then
            _pnr_host_ip="${PNR_CODEX_PROXY_HOST:-}"
            if [ -z "$_pnr_host_ip" ]; then
                _pnr_host_ip=$(ip route show default 2>/dev/null | awk '{print $3; exit}')
            fi
            if [ -n "$_pnr_host_ip" ]; then
                export HTTP_PROXY="http://$_pnr_host_ip:$PNR_CODEX_PROXY_PORT"
                export HTTPS_PROXY="$HTTP_PROXY"
                export ALL_PROXY="$HTTP_PROXY"
                export NO_PROXY="{{noProxy}}"
                _pnr_env="$CODEX_HOME/.env"
                if [ -f "$_pnr_env" ] && ! grep -q "^HTTP_PROXY=http://$_pnr_host_ip:$PNR_CODEX_PROXY_PORT$" "$_pnr_env"; then
                    _pnr_tmp=$(mktemp "${_pnr_env}.pnr-XXXXXX")
                    awk -v _proxy="http://$_pnr_host_ip:$PNR_CODEX_PROXY_PORT" -v _no_proxy='{{noProxy}}' '
                        BEGIN { _h=0; _s=0; _a=0; _n=0 }
                        /^[ \t]*(export[ \t]+)?HTTP_PROXY[ \t]*=/  { print "HTTP_PROXY=" _proxy;  _h=1; next }
                        /^[ \t]*(export[ \t]+)?HTTPS_PROXY[ \t]*=/ { print "HTTPS_PROXY=" _proxy; _s=1; next }
                        /^[ \t]*(export[ \t]+)?ALL_PROXY[ \t]*=/   { print "ALL_PROXY=" _proxy;   _a=1; next }
                        /^[ \t]*(export[ \t]+)?NO_PROXY[ \t]*=/    { print "NO_PROXY=" _no_proxy; _n=1; next }
                        { print }
                        END {
                            if (!_h) print "HTTP_PROXY=" _proxy
                            if (!_s) print "HTTPS_PROXY=" _proxy
                            if (!_a) print "ALL_PROXY=" _proxy
                            if (!_n) print "NO_PROXY=" _no_proxy
                        }' "$_pnr_env" > "$_pnr_tmp" && cat "$_pnr_tmp" > "$_pnr_env"
                    rm -f "$_pnr_tmp"
                fi
                unset _pnr_env _pnr_tmp
            fi
            unset _pnr_host_ip
        fi
        unset _pnr_codex_shell
        """);

    /// <summary>Marker line inside the boot runner by which the sync recognizes its own file.</summary>
    internal const string BootRunnerMarker = "PNR-BOOT-RUNNER";

    /// <summary>
    /// The boot runner next to the startup directory: /etc/wsl.d/startup/10-….sh →
    /// /etc/wsl.d/startup.sh. Manual string work: the paths are Linux-side and
    /// System.IO.Path would rewrite the separators on Windows.
    /// </summary>
    internal static string BootRunnerPath(string startupScriptPath)
    {
        var script = startupScriptPath.TrimEnd('/');
        var dirEnd = script.LastIndexOf('/');
        var startupDir = dirEnd > 0 ? script[..dirEnd] : "/etc/wsl.d/startup";
        var rootEnd = startupDir.LastIndexOf('/');
        return (rootEnd > 0 ? startupDir[..rootEnd] : "/etc") + "/startup.sh";
    }

    /// <summary>
    /// The runner executes every script of the startup directory as root at each distro start, so
    /// the directory must be a dedicated one: …/<parent>/startup/<script>. A script path such as
    /// /usr/local/bin/x.sh would make /usr/local/bin the startup directory and run everything in it.
    /// </summary>
    internal static bool IsDedicatedStartupScriptPath(string startupScriptPath)
    {
        var parts = startupScriptPath.Trim().Split('/');
        return startupScriptPath.StartsWith('/')
            && parts.Length >= 4
            && parts[^2] == "startup"
            && parts[^1].Length > 0;
    }

    /// <summary>The preserved foreign [boot] command, as the first script of the startup directory.</summary>
    internal static string UserCommandPath(string startupScriptPath)
    {
        var script = startupScriptPath.TrimEnd('/');
        var dirEnd = script.LastIndexOf('/');
        return (dirEnd > 0 ? script[..dirEnd] : "/etc/wsl.d/startup") + "/00-user-boot-command.sh";
    }

    private static string StartupDir(string startupScriptPath)
    {
        var script = startupScriptPath.TrimEnd('/');
        var dirEnd = script.LastIndexOf('/');
        return dirEnd > 0 ? script[..dirEnd] : "/etc/wsl.d/startup";
    }

    /// <summary>
    /// Value of the [boot] command key (unquoted); null when the section, the key or the
    /// value is missing or empty. Other sections and comment lines are ignored.
    /// </summary>
    internal static string? ExtractBootCommand(string? wslConfText)
    {
        if (string.IsNullOrEmpty(wslConfText))
        {
            return null;
        }

        var inBoot = false;
        foreach (var raw in wslConfText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                inBoot = line.Equals("[boot]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inBoot)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals("command", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\''))
            {
                value = value[1..^1].Trim();
            }

            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>
    /// Surgical edit: only the [boot] command line changes (normalized to command="…"),
    /// every other line survives byte for byte; a missing key is inserted after [boot],
    /// a missing section is appended, a missing file is created.
    /// </summary>
    internal static string SetBootCommand(string? original, string command)
    {
        var line = $"command=\"{command}\"";
        if (string.IsNullOrEmpty(original))
        {
            return $"[boot]\n{line}\n";
        }

        var lines = original.Split('\n').ToList();
        var boot = lines.FindIndex(l => l.Trim().Equals("[boot]", StringComparison.OrdinalIgnoreCase));
        if (boot < 0)
        {
            var head = original.EndsWith("\n") ? original : original + "\n";
            return $"{head}\n[boot]\n{line}\n";
        }

        var sectionEnd = lines.FindIndex(boot + 1, l => l.TrimStart().StartsWith('['));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        for (var i = boot + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0 || !trimmed[..eq].Trim().Equals("command", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines[i] = line;
            return string.Join('\n', lines);
        }

        lines.Insert(boot + 1, line);
        return string.Join('\n', lines);
    }

    /// <summary>The boot-layer plan for one distro from the state read inside it.</summary>
    internal static WslBootPlan PlanBootLayer(string runnerPath, string? bootCommand, WslBootRunnerState runnerState)
    {
        // A command that merely mentions the runner (`bash /etc/wsl.d/startup.sh`, `… &`,
        // `… >/dev/null`, a quoted path) already runs it: adopting it would preserve it as
        // 00-user-boot-command.sh, which the runner executes first — it would call the runner
        // again at every boot, endlessly. Such a command is left exactly as the owner wrote it.
        if (bootCommand is not null && bootCommand.Contains(runnerPath, StringComparison.Ordinal))
        {
            return runnerState == WslBootRunnerState.Ours ? WslBootPlan.AlreadyOurs : WslBootPlan.TakeoverRunner;
        }

        return string.IsNullOrWhiteSpace(bootCommand) ? WslBootPlan.InstallRunner : WslBootPlan.AdoptForeignCommand;
    }

    /// <summary>
    /// The boot runner the [boot] command of /etc/wsl.conf points at: it executes every
    /// executable script of the startup directory in name order (00-… first). Semantics of
    /// the hand-written wrapper of the pre-application scheme it replaces: the same boot
    /// log, one failing script does not stop the rest. The marker line is what the sync
    /// recognizes its own file by.
    /// </summary>
    internal static string BuildBootRunner(string startupDir) => NormalizeLf($$"""
        #!/bin/bash
        # {{BootRunnerMarker}}: managed by PrivateNetRouting (guard.wsl) — do not edit by hand.
        # Runs at WSL start (the [boot] command of /etc/wsl.conf points here) and executes
        # every executable script of {{startupDir}} in name order (00-… first).
        # Logs to /var/log/wsl-boot.log; one failing script does not stop the rest unless
        # FAIL_FAST=true is set in the environment.
        LOG_FILE="/var/log/wsl-boot.log"
        SCRIPT_DIR="{{startupDir}}"
        FAIL_FAST="${FAIL_FAST:-false}"

        export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

        log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1" >> "$LOG_FILE"; }

        [ -d "$SCRIPT_DIR" ] || mkdir -p "$SCRIPT_DIR"
        log "=== [START] WSL boot sequence (PrivateNetRouting runner) ==="

        shopt -s nullglob
        files=("$SCRIPT_DIR"/*)
        shopt -u nullglob

        for script in "${files[@]}"; do
            [ -f "$script" ] || continue
            name=$(basename "$script")
            case "$name" in
                *.pnr-bak-*|*.bak-*)
                    log "[SKIP] $name - backup, not a startup script"
                    continue
                    ;;
            esac
            if [ ! -x "$script" ]; then
                log "[SKIP] $name - not executable"
                continue
            fi
            log "--- [EXEC] $name ---"
            if "$script" >> "$LOG_FILE" 2>&1; then
                log "[OK] $name"
            else
                log "[FAIL] $name (exit $?)"
                if [ "$FAIL_FAST" = "true" ]; then
                    log "[ABORT] FAIL_FAST is on, stopping the sequence"
                    exit 1
                fi
            fi
        done

        log "=== [DONE] WSL boot sequence ==="
        """);

    /// <summary>The foreign [boot] command preserved as a script (the 00- name keeps it first).</summary>
    internal static string BuildUserCommandScript(string command) => NormalizeLf($$"""
        #!/bin/bash
        # The [boot] command previously written in /etc/wsl.conf, preserved by
        # PrivateNetRouting when its boot runner took over the wsl.conf entry.
        # The 00- name keeps it ahead of the other startup scripts.
        {{command}}
        """);

    /// <summary>
    /// One root pass for the whole boot layer: the runner (all plans), the preserved
    /// foreign command (adoption only) and the wsl.conf edit (when the command must
    /// change). Every block is the usual idempotent write: compare, backup, replace.
    /// </summary>
    private static string BuildBootSyncScript(
        WslBootPlan plan,
        string runnerPath,
        string userCommandPath,
        string startupDir,
        string? userCommand,
        string wslConfText,
        string stamp)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            {{PnrBakFn}}
            RUNNER="{{runnerPath}}"
            NEW="/tmp/.pnr-runner-{{stamp}}"
            echo '{{DistroPayload(BuildBootRunner(startupDir))}}' | base64 -d > "$NEW" || { echo "FAILED: cannot write the payload to /tmp"; exit 1; }
            mkdir -p "$(dirname "$RUNNER")"
            if [ -f "$RUNNER" ] && cmp -s "$NEW" "$RUNNER"; then
                echo RUNNER=unchanged
            else
                if [ -f "$RUNNER" ]; then pnr_bak "$RUNNER" BACKUP_RUNNER || { echo "FAILED: backup of $RUNNER"; exit 1; }; fi
                pnr_put "$NEW" "$RUNNER" || exit 1
                chmod 755 "$RUNNER"
                echo RUNNER=written
            fi
            rm -f "$NEW"
            ls -1t {{runnerPath}}.pnr-bak-* 2>/dev/null | tail -n +4 | xargs -r rm -f

            """);

        if (plan == WslBootPlan.AdoptForeignCommand && !string.IsNullOrWhiteSpace(userCommand))
        {
            sb.Append($$"""
                USERCMD="{{userCommandPath}}"
                NEW="/tmp/.pnr-usercmd-{{stamp}}"
                echo '{{DistroPayload(BuildUserCommandScript(userCommand))}}' | base64 -d > "$NEW" || { echo "FAILED: cannot write the payload to /tmp"; exit 1; }
                mkdir -p "$(dirname "$USERCMD")"
                if [ -f "$USERCMD" ] && cmp -s "$NEW" "$USERCMD"; then
                    echo USERCMD=unchanged
                else
                    if [ -f "$USERCMD" ]; then pnr_bak "$USERCMD" BACKUP_USERCMD || { echo "FAILED: backup of $USERCMD"; exit 1; }; fi
                    pnr_put "$NEW" "$USERCMD" || exit 1
                    chmod 755 "$USERCMD"
                    echo USERCMD=written
                fi
                rm -f "$NEW"
                ls -1t {{userCommandPath}}.pnr-bak-* 2>/dev/null | tail -n +4 | xargs -r rm -f

                """);
        }

        if (plan is WslBootPlan.InstallRunner or WslBootPlan.AdoptForeignCommand)
        {
            sb.Append($$"""
                WSLCONF="/etc/wsl.conf"
                NEW="/tmp/.pnr-wslconf-{{stamp}}"
                echo '{{DistroPayload(wslConfText)}}' | base64 -d > "$NEW" || { echo "FAILED: cannot write the payload to /tmp"; exit 1; }
                if [ -f "$WSLCONF" ] && cmp -s "$NEW" "$WSLCONF"; then
                    echo WSLCONF=unchanged
                else
                    if [ -f "$WSLCONF" ]; then pnr_bak "$WSLCONF" BACKUP_WSLCONF || { echo "FAILED: backup of $WSLCONF"; exit 1; }; fi
                    pnr_put "$NEW" "$WSLCONF" || exit 1
                    chmod 644 "$WSLCONF"
                    echo WSLCONF=written
                fi
                rm -f "$NEW"
                ls -1t /etc/wsl.conf.pnr-bak-* 2>/dev/null | tail -n +4 | xargs -r rm -f

                """);
        }

        sb.Append("echo PNR=ok\n");
        return NormalizeLf(sb.ToString());
    }

    /// <summary>Startup script text (proxy address and port come from the application config).</summary>
    internal static string BuildStartupScript(string user, int port, string? fixedHost = null) => NormalizeLf($$"""
        #!/bin/bash
        # Managed by PrivateNetRouting (guard.wslStartupScript). Keeps the Claude Code proxy
        # env pointing at the Windows side of the WSL NAT gateway, where the application's
        # forward proxy listens. Runs from /etc/wsl.d/startup.sh at WSL start (as root);
        # safe to run by hand. Idempotent: NO_PROXY is not touched (it is synced from
        # Windows by the app), only HTTPS_PROXY/HTTP_PROXY are refreshed.
        TAG="[10-claude-proxy]"
        USER_NAME='{{user}}'
        USER_HOME=$(getent passwd "$USER_NAME" | cut -d: -f6)
        [ -n "$USER_HOME" ] || USER_HOME="/home/$USER_NAME"
        SETTINGS="$USER_HOME/.claude/settings.json"
        PORT={{port}}
        {{(fixedHost is null ? "" : $"PNR_PROXY_HOST='{fixedHost}'")}}

        # Host IP: the external upstream's own address when the application's endpoint is one
        # (PNR_PROXY_HOST above), otherwise the default gateway of the WSL NAT. resolv.conf is
        # not used: with dnsTunneling enabled it holds 10.255.255.4/254, not the host address.
        HOST_IP="${PNR_PROXY_HOST:-}"
        if [ -z "$HOST_IP" ]; then
            for i in 1 2 3 4 5; do
                HOST_IP=$(ip route show default 2>/dev/null | awk '{print $3; exit}')
                [ -n "$HOST_IP" ] && break
                sleep 2
            done
        fi
        if [ -z "$HOST_IP" ]; then
            echo "$TAG default route not found - exiting without changes"
            exit 0
        fi
        PROXY="http://$HOST_IP:$PORT"

        # Proxy liveness does not gate the config write: the Windows chain may come up later.
        if timeout 3 bash -c "</dev/tcp/$HOST_IP/$PORT" 2>/dev/null; then
            echo "$TAG proxy $PROXY responds"
        else
            echo "$TAG proxy $PROXY not responding yet (host chain coming up?) - writing the config anyway"
        fi

        # The script runs as root: a directory created here must belong to the user (Claude
        # Code, running as the user, creates projects/, todos/ and credentials inside it), and
        # the primary group is looked up — it is not always named like the user.
        USER_GROUP=$(id -gn "$USER_NAME" 2>/dev/null || echo "$USER_NAME")
        [ -d "$USER_HOME/.claude" ] || install -d -o "$USER_NAME" -g "$USER_GROUP" -m 755 "$USER_HOME/.claude"
        # An existing settings.json that cannot be parsed is never rewritten: it holds the
        # owner's permissions, hooks and model, and a boot-time writer has no way to ask.
        # The file is replaced through a temp file, so a failure never leaves it half written.
        if python3 - "$SETTINGS" "$PROXY" <<'PYEOF'
        import json, os, sys
        path, proxy = sys.argv[1], sys.argv[2]
        real = os.path.realpath(path)
        data = {}
        if os.path.exists(real):
            try:
                # utf-8-sig: a BOM from a Windows editor is not a parse error
                with open(real, encoding='utf-8-sig') as f:
                    text = f.read()
                if text.strip():
                    data = json.loads(text)
                if not isinstance(data, dict) or not isinstance(data.get('env', {}), dict):
                    raise ValueError('the top level or "env" is not an object')
            except Exception as e:
                print('cannot parse %s (%s)' % (path, e))
                sys.exit(3)
        env = data.setdefault('env', {})
        env['HTTPS_PROXY'] = proxy
        env['HTTP_PROXY'] = proxy
        tmp = real + '.pnr-tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
        if os.path.exists(real):
            st = os.stat(real)
            os.chmod(tmp, st.st_mode & 0o7777)
            os.chown(tmp, st.st_uid, st.st_gid)
        os.replace(tmp, real)
        PYEOF
        then
            chown "$USER_NAME:$USER_GROUP" "$SETTINGS" 2>/dev/null
            echo "$TAG proxy $PROXY written to $SETTINGS"
        else
            echo "$TAG $SETTINGS left untouched - fix it by hand, the next WSL start retries"
            exit 1
        fi
        """);

    private static GuardState Aggregate(IReadOnlyList<WslDistroStatus> distros)
    {
        if (distros.Any(d => d.State == GuardState.Unknown))
        {
            return GuardState.Unknown;
        }

        if (distros.All(d => d.State == GuardState.On))
        {
            return GuardState.On;
        }

        return distros.All(d => d.State == GuardState.Off) ? GuardState.Off : GuardState.Partial;
    }

    private static string DescribeMismatch(WslRead read, int expectedPort, string? expectedHost, string? expectedProxy, bool proxyEnvExpected)
    {
        var parts = new List<string>();
        if (read.ScriptPort != expectedPort)
        {
            parts.Add($"script PORT={read.ScriptPort ?? -1} (expected {expectedPort})");
        }

        if (read.ScriptHost != expectedHost)
        {
            parts.Add($"script host={read.ScriptHost ?? "<gateway>"} (expected {expectedHost ?? "<gateway>"})");
        }

        if (expectedProxy is not null && read.SettingsProxy != expectedProxy)
        {
            parts.Add($"settings HTTPS_PROXY={read.SettingsProxy ?? "<none>"} (expected {expectedProxy})");
        }

        if (read.SettingsProxy is null && expectedProxy is null)
        {
            parts.Add("settings.json has no HTTPS_PROXY");
        }

        if (proxyEnvExpected && !read.ProxyEnvPresent)
        {
            parts.Add("codex proxy env file not found");
        }
        else if (proxyEnvExpected && read.ProxyEnvPort != expectedPort)
        {
            parts.Add($"codex proxy env PORT={read.ProxyEnvPort?.ToString() ?? "<none>"} (expected {expectedPort})");
        }
        else if (proxyEnvExpected && read.ProxyEnvHost != expectedHost)
        {
            parts.Add($"codex proxy env host={read.ProxyEnvHost ?? "<gateway>"} (expected {expectedHost ?? "<gateway>"})");
        }

        return string.Join("; ", parts);
    }
}
