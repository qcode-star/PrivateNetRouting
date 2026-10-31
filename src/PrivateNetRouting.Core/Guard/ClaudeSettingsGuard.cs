using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Guard;

/// <summary>Expected env values for settings.json (Windows and WSL are built the same way, except the proxy address).</summary>
public static class ClaudeEnvValues
{
    /// <summary>The proxy URL the Windows clients are pointed at (the applications proxy endpoint).</summary>
    public static string WindowsProxyUrl(AppConfig config) => AppProxyEndpoint.Url(config);

    /// <summary>
    /// The host the distros must dial when the applications endpoint is an external upstream
    /// (the local proxy is skipped) on a routable address: that address itself — the WSL NAT
    /// gateway has nothing listening on the upstream's port. Null — the endpoint lives on this
    /// Windows host (the local proxy, or a loopback upstream), reached through the gateway.
    /// </summary>
    public static string? WslFixedHost(AppConfig config)
    {
        if (!AppProxyEndpoint.IsSkipped(config))
        {
            return null;
        }

        var address = AppProxyEndpoint.EndPoint(config).Address;
        return System.Net.IPAddress.IsLoopback(address) ? null : address.ToString();
    }

    /// <summary>
    /// Proxy address for WSL: for a host-local endpoint the IP of the Windows side of WSL NAT
    /// (vEthernet (WSL...)) — not 127.0.0.1; for an external upstream (<see cref="WslFixedHost"/>)
    /// the upstream's own address. The port is the applications proxy endpoint's.
    /// </summary>
    public static string? WslProxyUrl(System.Net.IPAddress? gateway, AppConfig config) =>
        WslFixedHost(config) is { } host
            ? $"http://{UrlHost(host)}:{Port(config)}"
            : gateway is null ? null : $"http://{UrlHost(gateway.ToString())}:{Port(config)}";

    /// <summary>A literal IP as the host part of a URL: an IPv6 address goes in brackets (RFC 3986).</summary>
    public static string UrlHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    public static int Port(AppConfig config) => AppProxyEndpoint.Port(config);

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
/// settings.json of Claude Code, Windows: the maintained shape (the proxy env — the Claude Code
/// engine, CLI and the one built into Desktop, applies env both to sessions and to commands
/// inside them — plus the privacy switches and the attribution keys, see ClaudeSettingsShape).
/// Existing keys (env and root) are preserved; the file is rewritten only when the content
/// actually differs, and before a rewrite the previous version is preserved unless an identical
/// copy is already among the `.pnr-bak-*` backups — the matching copy is touched instead, so
/// mtime order mirrors the states most recently preserved (every copy gets its mtime at
/// preservation time). The backups live in three numbered slots, the one preserved longest ago
/// is reused; the legacy `.bak-&lt;timestamp&gt;` naming is cleaned up on every sync. Requires no
/// special rights.
/// </summary>
public sealed class ClaudeSettingsGuard(AppConfig config)
{
    public ClaudeSettingsStatus Read()
    {
        var expectedProxy = ClaudeEnvValues.WindowsProxyUrl(config);
        var expectedNoProxy = ClaudeEnvValues.BuildNoProxy(config.BypassDomains);
        if (!File.Exists(config.Claude.SettingsPath))
        {
            return new ClaudeSettingsStatus(GuardState.Off, null, null, expectedProxy, expectedNoProxy, FileMissing: true);
        }

        try
        {
            var root = ReadRootOrEmpty(config.Claude.SettingsPath);
            var env = ReadEnvOf(root);
            var proxy = ClaudeSettingsShape.Text(env?["HTTPS_PROXY"]);
            var noProxy = ClaudeSettingsShape.Text(env?["NO_PROXY"]);
            var proxyOk = proxy == expectedProxy && ClaudeSettingsShape.Text(env?["HTTP_PROXY"]) == expectedProxy;
            var noProxyOk = noProxy == expectedNoProxy;
            var drift = ClaudeSettingsShape.DriftOf(root);
            var state = proxyOk && noProxyOk && drift.Count == 0 ? GuardState.On : GuardState.Partial;
            return new ClaudeSettingsStatus(state, proxy, noProxy, expectedProxy, expectedNoProxy, FileMissing: false, Drift: drift);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            // a malformed file (a non-object root included) is reported as Unknown and left alone
            // (Q-02): the guard never rewrites what it could not read
            return new ClaudeSettingsStatus(GuardState.Unknown, null, null, expectedProxy, expectedNoProxy, FileMissing: false)
            {
                Detail = ex.Message,
            };
        }
    }

    /// <summary>Outcome of a settings sync: whether the file was rewritten and which backup was made.</summary>
    public sealed record ClaudeSettingsSyncResult(bool Written, string? BackupPath);

    /// <summary>Writes the maintained shape (proxy env + privacy switches + attribution); the file is touched only on an actual change.</summary>
    public ClaudeSettingsSyncResult Sync()
    {
        var path = config.Claude.SettingsPath;
        var root = ReadRootOrEmpty(path);
        // like the distro boot script: an "env" that is not an object is the owner's to fix — the
        // file is left untouched rather than having it replaced
        if (root.ContainsKey("env") && root["env"] is not JsonObject)
        {
            throw new InvalidDataException($"settings.json \"env\" is not a JSON object: {path} — fix it by hand, the file is left untouched");
        }

        var written = ClaudeSettingsShape.Apply(root, ClaudeEnvValues.WindowsProxyUrl(config), ClaudeEnvValues.BuildNoProxy(config.BypassDomains));
        string? backup = null;
        if (written)
        {
            backup = Backup(path);
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

    /// <summary>
    /// Preserves the file before a rewrite: the copy goes to a `<name>.pnr-bak-<n>` name
    /// (three numbered slots; the one preserved longest ago is reused when all are taken). A state
    /// already present among the `.pnr-bak-*` copies is not copied again — the matching copy is
    /// touched instead, so mtime order mirrors the states most recently preserved; fresh copies
    /// are touched too (File.Copy keeps the source mtime). Null — the file did not exist or its
    /// content is already preserved.
    /// </summary>
    internal static string? Backup(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(path) ?? ".";
        var prefix = $"{Path.GetFileName(path)}.pnr-bak-";
        var current = File.ReadAllBytes(path);
        string? reuse = null;
        var oldest = DateTime.MaxValue;
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in Directory.GetFiles(dir, $"{prefix}*"))
        {
            if (current.SequenceEqual(File.ReadAllBytes(existing)))
            {
                // the state is already preserved — refresh its recency instead of copying again
                File.SetLastWriteTimeUtc(existing, DateTime.UtcNow);
                return null;
            }

            var suffix = Path.GetFileName(existing)[prefix.Length..];
            if (suffix is "1" or "2" or "3")
            {
                taken.Add(suffix);
                var preserved = File.GetLastWriteTimeUtc(existing);
                if (preserved < oldest)
                {
                    oldest = preserved;
                    reuse = existing;
                }
            }
        }

        // a free slot first; only when all three are taken — the one preserved longest ago
        var slot = taken.Count == 3
            ? reuse!
            : $"{path}.pnr-bak-" + new[] { "1", "2", "3" }.First(n => !taken.Contains(n));
        File.Copy(path, slot, overwrite: true);
        File.SetLastWriteTimeUtc(slot, DateTime.UtcNow);
        return slot;
    }

    /// <summary>
    /// Keeps the three most recently preserved `.pnr-bak-` copies (mtime order — the copies may
    /// carry legacy timestamp names); removes the legacy `.bak-<timestamp>` naming.
    /// </summary>
    internal static void PruneBackups(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileName(path);
        var backups = Directory.GetFiles(dir, $"{name}.pnr-bak-*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        foreach (var stale in backups.Skip(3))
        {
            File.Delete(stale);
        }

        foreach (var stale in Directory.GetFiles(dir, $"{name}.bak-2*"))
        {
            File.Delete(stale);
        }
    }

    private static void Write(string path, JsonObject root) =>
        AtomicFile.Write(path, root.ToJsonString(JsonOptions));

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions JsonDocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
}
