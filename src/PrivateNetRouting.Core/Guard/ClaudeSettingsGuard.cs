using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Guard;

/// <summary>Expected env values for settings.json (Windows and WSL are built the same way, except the proxy address).</summary>
public static class ClaudeEnvValues
{
    public static string WindowsProxyUrl(ForwardProxyConfig fp) => "http://" + fp.Listen;

    /// <summary>Proxy address for WSL: IP of the Windows side of WSL NAT (vEthernet (WSL...)) — not 127.0.0.1.</summary>
    public static string? WslProxyUrl(System.Net.IPAddress? gateway, ForwardProxyConfig fp) =>
        gateway is null ? null : $"http://{gateway}:{Port(fp)}";

    public static int Port(ForwardProxyConfig fp) => Net.Socks.Socks5Client.ParseEndPoint(fp.Listen).Port;

    /// <summary>
    /// NO_PROXY (curl style): base entries + each zone in two forms — "zone" and ".zone"
    /// (subdomains). Zones come from launcher.bypassDomains.
    /// </summary>
    public static string BuildNoProxy(IEnumerable<string> bypassDomains) =>
        string.Join(",", NoProxyParts(bypassDomains));

    public static IReadOnlyList<string> NoProxyParts(IEnumerable<string> bypassDomains)
    {
        var parts = new List<string> { "localhost", "127.0.0.1", ".local" };
        foreach (var domain in bypassDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            parts.Add(domain);
            parts.Add("." + domain);
        }

        return parts;
    }
}

/// <summary>
/// env in settings.json of Claude Code, Windows: the Claude Code
/// engine (CLI and the one built into Desktop) applies env both to sessions and to commands inside
/// them. Existing keys (env and root) are preserved; the file is rewritten only when the env
/// actually differs, and before a rewrite the previous version is copied to a `.pnr-bak-<timestamp>`
/// name (at most three such copies are kept; the legacy `.bak-<timestamp>` naming is cleaned up on
/// every sync). Requires no special rights.
/// </summary>
public sealed class ClaudeSettingsGuard(AppConfig config)
{
    public ClaudeSettingsStatus Read()
    {
        var expectedProxy = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy);
        var expectedNoProxy = ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains);
        if (!File.Exists(config.Guard.ClaudeSettingsPath))
        {
            return new ClaudeSettingsStatus(GuardState.Off, null, null, expectedProxy, expectedNoProxy, FileMissing: true);
        }

        try
        {
            var env = ReadEnv();
            var proxy = env?["HTTPS_PROXY"]?.GetValue<string>();
            var noProxy = env?["NO_PROXY"]?.GetValue<string>();
            var proxyOk = proxy == expectedProxy && env?["HTTP_PROXY"]?.GetValue<string>() == expectedProxy;
            var noProxyOk = noProxy == expectedNoProxy;
            var state = proxyOk && noProxyOk ? GuardState.On : GuardState.Partial;
            return new ClaudeSettingsStatus(state, proxy, noProxy, expectedProxy, expectedNoProxy, FileMissing: false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ClaudeSettingsStatus(GuardState.Unknown, null, null, expectedProxy, expectedNoProxy, FileMissing: false)
            {
                Detail = ex.Message,
            };
        }
    }

    /// <summary>Outcome of a settings sync: whether the file was rewritten and which backup was made.</summary>
    public sealed record ClaudeSettingsSyncResult(bool Written, string? BackupPath);

    /// <summary>Writes env (HTTPS_PROXY/HTTP_PROXY/NO_PROXY); the file is touched only on an actual change.</summary>
    public ClaudeSettingsSyncResult Sync()
    {
        var path = config.Guard.ClaudeSettingsPath;
        var root = ReadRootOrEmpty(path);
        var env = root["env"] as JsonObject ?? [];
        var proxy = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy);
        var noProxy = ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains);
        string? backup = null;
        var written = !(env["HTTPS_PROXY"]?.GetValue<string>() == proxy
            && env["HTTP_PROXY"]?.GetValue<string>() == proxy
            && env["NO_PROXY"]?.GetValue<string>() == noProxy);
        if (written)
        {
            backup = Backup(path);
            env["HTTPS_PROXY"] = proxy;
            env["HTTP_PROXY"] = proxy;
            env["NO_PROXY"] = noProxy;
            root["env"] = env;
            Write(path, root);
        }

        // after the new copy exists, so "at most three" counts it
        PruneBackups(path);
        return new ClaudeSettingsSyncResult(written, backup);
    }

    internal static JsonObject ReadRootOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonNode.Parse(text, documentOptions: JsonDocOptions) as JsonObject
            ?? throw new InvalidDataException($"settings.json is not a JSON object: {path}");
    }

    internal static JsonObject? ReadEnvOf(JsonObject root) => root["env"] as JsonObject;

    private JsonObject? ReadEnv() => File.Exists(config.Guard.ClaudeSettingsPath)
        ? ReadEnvOf(ReadRootOrEmpty(config.Guard.ClaudeSettingsPath))
        : null;

    /// <summary>Copies the file to a `.pnr-bak-<timestamp>` name (null — the file did not exist).</summary>
    internal static string? Backup(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var backupPath = $"{path}.pnr-bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }

    /// <summary>Keeps the three newest `.pnr-bak-` copies; removes the legacy `.bak-<timestamp>` naming.</summary>
    internal static void PruneBackups(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileName(path);
        var backups = Directory.GetFiles(dir, $"{name}.pnr-bak-*").OrderDescending().ToList();
        foreach (var stale in backups.Skip(3))
        {
            File.Delete(stale);
        }

        foreach (var stale in Directory.GetFiles(dir, $"{name}.bak-2*"))
        {
            File.Delete(stale);
        }
    }

    private static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions JsonDocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
}
