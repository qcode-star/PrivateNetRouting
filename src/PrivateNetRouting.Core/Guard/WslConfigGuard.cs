using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Net;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Core.Guard;

/// <summary>Status of one distro from guard.wsl.distros.</summary>
public sealed record WslDistroStatus(
    string Distro,
    bool Sync,
    GuardState State,
    int? ScriptPort,
    string? CurrentProxy,
    string Reason);

/// <summary>Status of the WSL layer: off (Reason explains) or an aggregate over distros.</summary>
public sealed record WslConfigStatus(
    GuardState State,
    IReadOnlyList<WslDistroStatus> Distros,
    int ExpectedPort,
    string? ExpectedProxy,
    string Reason)
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
    string? ScriptBackup);

public sealed record WslSyncResult(bool LayerDisabled, string? Reason, IReadOnlyList<WslDistroSyncResult> Distros);

/// <summary>
/// WSL layer of the guard. The layer is entirely optional: guard.wsl.mode = on | off | auto
/// (auto — on only if WSL is present and at least one configured distro is installed).
/// Per distro from guard.wsl.distros there is a separate choice of whether to sync into it:
/// sync=true — startup script (proxy address = IP of the Windows side of WSL NAT + application port;
/// NO_PROXY is not touched — it is managed by the sync) and the full env in ~/.claude/settings.json
/// (proxy + NO_PROXY), with backups; sync=false — status only.
/// Status is read-only and only in running distros (they are not started just for a check);
/// sync does not start a distro — it skips it with a reason.
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

        var expectedPort = ClaudeEnvValues.Port(config.ForwardProxy);
        var expectedProxy = ClaudeEnvValues.WslProxyUrl(_gateway(), config.ForwardProxy);
        var running = await ListDistrosAsync("--running", ct);
        var installed = await ListDistrosAsync("--quiet", ct);

        var distros = new List<WslDistroStatus>();
        foreach (var distro in ConfiguredDistros())
        {
            distros.Add(await ReadDistroAsync(distro, running, installed, expectedPort, expectedProxy, ct));
        }

        return new WslConfigStatus(Aggregate(distros), distros, expectedPort, expectedProxy, "");
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
        foreach (var distro in ConfiguredDistros().Where(d => d.Sync))
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

            results.Add(await SyncDistroAsync(distro, gateway, ct));
        }

        return new WslSyncResult(false, null, results);
    }

    /// <summary>Distros with the required fields; the config order is preserved.</summary>
    internal IReadOnlyList<WslDistroConfig> ConfiguredDistros() =>
        config.Guard.Wsl.Distros
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.User))
            .ToList();

    /// <summary>
    /// Layer on/off: off — always off; an empty list of configured distros — off;
    /// on — on; auto — on only if WSL responds and at least one configured distro
    /// is installed (running wsl with no distros prints a message instead of names — no name
    /// will match, output localization does not matter).
    /// </summary>
    internal async Task<(bool Enabled, string Reason)> EvaluateLayerAsync(CancellationToken ct)
    {
        var mode = config.Guard.Wsl.Mode.Trim().ToLowerInvariant();
        var configured = ConfiguredDistros();
        if (mode == "off")
        {
            return (false, "wsl guard is disabled (guard.wsl.mode=off)");
        }

        if (configured.Count == 0)
        {
            return (false, "no distros configured (guard.wsl.distros)");
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
            return new WslDistroStatus(distro.Name, distro.Sync, GuardState.Unknown, null, null, "distro is not installed");
        }

        if (!running.Contains(distro.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new WslDistroStatus(distro.Name, distro.Sync, GuardState.Unknown, null, null, "distro is not running");
        }

        try
        {
            var read = await ReadInsideAsync(distro, ct);
            if (!read.ScriptPresent)
            {
                return new WslDistroStatus(distro.Name, distro.Sync, GuardState.Off, null, read.SettingsProxy, "startup script not found");
            }

            var portOk = read.ScriptPort == expectedPort;
            var proxyOk = expectedProxy is null || read.SettingsProxy == expectedProxy;
            var reason = portOk && proxyOk
                ? ""
                : DescribeMismatch(read, expectedPort, expectedProxy);
            return new WslDistroStatus(
                distro.Name,
                distro.Sync,
                portOk && proxyOk ? GuardState.On : GuardState.Partial,
                read.ScriptPort,
                read.SettingsProxy,
                reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            return new WslDistroStatus(distro.Name, distro.Sync, GuardState.Unknown, null, null, $"failed to read: {ex.Message}");
        }
    }

    private async Task<WslDistroSyncResult> SyncDistroAsync(WslDistroConfig distro, System.Net.IPAddress? gateway, CancellationToken ct)
    {
        var proxy = ClaudeEnvValues.WslProxyUrl(gateway, config.ForwardProxy)
            ?? throw new IOException($"WSL NAT gateway (vEthernet) not found for '{distro.Name}' — is WSL up?");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var read = await ReadInsideAsync(distro, ct);

        // distro settings.json: full env (gateway proxy + NO_PROXY), foreign keys are preserved
        var root = read.SettingsRoot ?? [];
        var env = root["env"] as JsonObject ?? [];
        env["HTTPS_PROXY"] = proxy;
        env["HTTP_PROXY"] = proxy;
        env["NO_PROXY"] = ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains);
        root["env"] = env;
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var settingsScript = $"""
            mkdir -p ~/.claude
            if [ -f ~/.claude/settings.json ]; then cp -p ~/.claude/settings.json ~/.claude/settings.json.bak-{stamp}; echo BACKUP=settings.json.bak-{stamp}; fi
            echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}' | base64 -d > ~/.claude/settings.json
            """;
        await wsl.RunAsync(["-d", distro.Name, "-u", distro.User, "--", "bash", "-s"], settingsScript, Encoding.UTF8, ct);

        // startup script: refreshes the proxy address on every distro start
        var scriptPath = config.Guard.WslStartupScript;
        var scriptContent = BuildStartupScript(distro.User, ClaudeEnvValues.Port(config.ForwardProxy));
        var rootScript = $"""
            if [ -f {scriptPath} ]; then cp -p {scriptPath} {scriptPath}.bak-{stamp}; echo BACKUP={scriptPath}.bak-{stamp}; fi
            echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptContent))}' | base64 -d > {scriptPath}
            chmod 755 {scriptPath}
            """;
        await wsl.RunAsync(["-d", distro.Name, "-u", "root", "--", "bash", "-s"], rootScript, Encoding.UTF8, ct);

        return new WslDistroSyncResult(
            distro.Name,
            false, null, proxy,
            SettingsWritten: true, SettingsBackup: read.SettingsPresent ? $"~/.claude/settings.json.bak-{stamp}" : null,
            ScriptWritten: true, ScriptBackup: read.ScriptPresent ? $"{scriptPath}.bak-{stamp}" : null);
    }

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
        JsonObject? SettingsRoot);

    /// <summary>One trip into the distro for all state (root: sees both /etc and home).</summary>
    private async Task<WslRead> ReadInsideAsync(WslDistroConfig distro, CancellationToken ct)
    {
        var scriptPath = config.Guard.WslStartupScript;
        var settingsPath = $"/home/{distro.User}/.claude/settings.json";
        var probe = $"""
            if [ -f {scriptPath} ]; then
                echo SCRIPT=present
                grep -E '^PORT=' {scriptPath} 2>/dev/null || true
                grep -E '^USER_NAME=' {scriptPath} 2>/dev/null || true
            else
                echo SCRIPT=missing
            fi
            if [ -f {settingsPath} ]; then
                echo SETTINGS=present
                cat {settingsPath}
                echo
                echo SETTINGS=END
            else
                echo SETTINGS=missing
            fi
            """;
        var output = await wsl.RunAsync(["-d", distro.Name, "-u", "root", "--", "bash", "-s"], probe, Encoding.UTF8, ct);
        return ParseRead(output);
    }

    internal static WslRead ParseRead(string output)
    {
        bool scriptPresent = false, settingsFilePresent = false;
        int? port = null;
        string? user = null, proxy = null;
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
                    else if (line.StartsWith("USER_NAME=", StringComparison.Ordinal))
                    {
                        user = line["USER_NAME=".Length..].Trim().Trim('\'', '"');
                    }

                    break;
            }
        }

        return new WslRead(scriptPresent, port, user, settingsFilePresent, proxy, root);
    }

    /// <summary>Startup script text (proxy address and port come from the application config).</summary>
    internal static string BuildStartupScript(string user, int port) => $$"""
        #!/bin/bash
        # Managed by PrivateNetRouting (guard.wslStartupScript). Keeps the Claude Code proxy
        # env pointing at the Windows side of the WSL NAT gateway, where the application's
        # forward proxy listens. Runs from /etc/wsl.d/startup.sh at WSL start (as root);
        # safe to run by hand. Idempotent: NO_PROXY is not touched (it is synced from
        # Windows by the app), only HTTPS_PROXY/HTTP_PROXY are refreshed.
        TAG="[10-claude-proxy]"
        USER_NAME='{{user}}'
        USER_HOME="/home/$USER_NAME"
        SETTINGS="$USER_HOME/.claude/settings.json"
        PORT={{port}}

        # Host IP = default gateway of the WSL NAT. resolv.conf is not used: with
        # dnsTunneling enabled it holds 10.255.255.4/254, not the host address.
        HOST_IP=""
        for i in 1 2 3 4 5; do
            HOST_IP=$(ip route show default 2>/dev/null | awk '{print $3; exit}')
            [ -n "$HOST_IP" ] && break
            sleep 2
        done
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
        echo "$TAG proxy $PROXY written to $SETTINGS"
        """;

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

    private static string DescribeMismatch(WslRead read, int expectedPort, string? expectedProxy)
    {
        var parts = new List<string>();
        if (read.ScriptPort != expectedPort)
        {
            parts.Add($"script PORT={read.ScriptPort ?? -1} (expected {expectedPort})");
        }

        if (expectedProxy is not null && read.SettingsProxy != expectedProxy)
        {
            parts.Add($"settings HTTPS_PROXY={read.SettingsProxy ?? "<none>"} (expected {expectedProxy})");
        }

        if (read.SettingsProxy is null && expectedProxy is null)
        {
            parts.Add("settings.json has no HTTPS_PROXY");
        }

        return string.Join("; ", parts);
    }
}
