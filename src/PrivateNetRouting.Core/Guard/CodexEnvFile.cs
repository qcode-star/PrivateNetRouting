using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// The proxy env part of the Codex guard layer: the file `&lt;CODEX_HOME&gt;/.env` (guard.codexEnvPath).
/// The embedded CLI core loads this file after its own start and — unlike a plain dotenv load —
/// applies the values over the inherited environment (only `CODEX_*` names are protected), so this
/// file is what actually carries the proxy to the core: the desktop app strips the inherited proxy
/// env variables from the processes it spawns, and on the WSL side the exported profile values are
/// overridden by it too. The Windows value is the loopback form; the WSL layer rewrites the file to
/// the NAT gateway address in the shells Codex runs there, and the launcher refreshes it back
/// before every launch (a stale gateway address is unreachable once WSL is down).
/// </summary>
public static class CodexEnvFile
{
    /// <summary>The maintained keys, in the file order.</summary>
    public static readonly IReadOnlyList<string> Keys = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];

    /// <summary>The values the Windows side writes: the forward proxy address and the bypass list.</summary>
    public static IReadOnlyDictionary<string, string> Values(AppConfig config) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["HTTP_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config),
        ["HTTPS_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config),
        ["ALL_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config),
        ["NO_PROXY"] = ClaudeEnvValues.BuildNoProxy(config.BypassDomains),
    };

    /// <summary>
    /// The keys' lines rewritten/added in an .env text; foreign lines survive byte for byte, the
    /// newline style is kept, the text always ends with a newline. null/empty input — a fresh file
    /// with just our keys.
    /// </summary>
    public static string Apply(string? existing, IReadOnlyDictionary<string, string> values)
    {
        var newline = existing is not null && existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = existing is null || existing.Length == 0
            ? []
            : existing.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1); // a trailing newline is not a line
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var key = KeyOf(lines[i]);
            if (key is null)
            {
                continue;
            }

            // any spelling of a maintained key (http_proxy, export HTTP_PROXY=…) is recognized and
            // rewritten to the canonical form: the maintained file shape is uppercase
            var canonical = Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (canonical is null || !values.TryGetValue(canonical, out var value))
            {
                continue;
            }

            lines[i] = $"{canonical}={value}";
            seen.Add(canonical);
        }

        foreach (var (key, value) in values)
        {
            if (!seen.Contains(key))
            {
                lines.Add($"{key}={value}");
            }
        }

        return string.Join(newline, lines) + newline;
    }

    /// <summary>
    /// Whether the text holds every maintained key with the expected value. The proxy addresses
    /// accept both resting forms the tooling writes: the exact Windows values, or the WSL gateway
    /// form the profile snippet leaves behind after a codex session in the distro (same port, a
    /// private IPv4 host). The file has no single form at rest — every consumer refreshes it
    /// before use (the Windows launcher back to the loopback, the WSL snippet to the gateway), so
    /// a gateway form at rest is not stale. NO_PROXY must still match exactly.
    /// </summary>
    public static bool UpToDate(string? text, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var entry = EntryLineOf(line.TrimEnd('\r'));
            if (entry is { } item && !found.ContainsKey(item.Key)
                && Keys.Any(k => k.Equals(item.Key, StringComparison.OrdinalIgnoreCase)))
            {
                found[item.Key] = item.Value;
            }
        }

        return values.All(pair => found.TryGetValue(pair.Key, out var value)
            && (value == pair.Value || IsWslGatewayForm(value, pair.Value)));
    }

    /// <summary>
    /// The WSL form of a maintained value: the same scheme and port as the expected Windows value,
    /// but the host is a private IPv4 — the NAT gateway address the profile snippet computes
    /// inside the distro. It rotates between WSL restarts, so "the current gateway" cannot be
    /// demanded; a foreign port, a hostname or a public host is still a mismatch. A non-URL value
    /// (NO_PROXY) never matches this form.
    /// </summary>
    private static bool IsWslGatewayForm(string value, string expected)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != "http"
            || !Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri)
            || uri.Port != expectedUri.Port
            || !System.Net.IPAddress.TryParse(uri.Host, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = ip.GetAddressBytes();
        return octets[0] == 10
            || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192 && octets[1] == 168);
    }

    /// <summary>
    /// Writes the file (the loopback form) unless it already holds exactly these values; the
    /// previous version goes to a `.pnr-bak-&lt;n&gt;` copy unless an identical state is already
    /// preserved (three numbered slots are kept).
    /// </summary>
    public static (bool Written, string? BackupPath) Write(AppConfig config)
    {
        var values = Values(config);
        var path = config.Codex.EnvPath;
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var updated = Apply(existing, values);
        if (existing == updated)
        {
            ClaudeSettingsGuard.PruneBackups(path);
            return (false, null);
        }

        var backup = ClaudeSettingsGuard.Backup(path);
        AtomicFile.Write(path, updated);
        ClaudeSettingsGuard.PruneBackups(path);
        return (true, backup);
    }

    /// <summary>Key of a `KEY=value` or `export KEY=value` line; null — a comment or not a key line.</summary>
    internal static string? KeyOf(string line) => EntryLineOf(line)?.Key;

    /// <summary>The `KEY=value` entry of a line, the `export ` prefix stripped; null — a comment or not a key line.</summary>
    private static (string Key, string Value)? EntryLineOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["export ".Length..].TrimStart();
        }

        return EntryOf(trimmed);
    }

    private static (string Key, string Value)? EntryOf(string trimmed)
    {
        if (trimmed.StartsWith('#'))
        {
            return null;
        }

        var eq = trimmed.IndexOf('=');
        if (eq <= 0)
        {
            return null;
        }

        var key = trimmed[..eq].Trim();
        return key.Length == 0 ? null : (key, trimmed[(eq + 1)..].Trim());
    }
}
