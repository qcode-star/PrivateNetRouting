using PrivateNetRouting.Core.Configuration;

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
    public static IReadOnlyDictionary<string, string> Values(AppConfig config) => new Dictionary<string, string>
    {
        ["HTTP_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy),
        ["HTTPS_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy),
        ["ALL_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy),
        ["NO_PROXY"] = ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains),
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
            if (key is null || !values.TryGetValue(key, out var value))
            {
                continue;
            }

            lines[i] = $"{key}={value}";
            seen.Add(key);
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

    /// <summary>Whether the text holds every maintained key with the expected value.</summary>
    public static bool UpToDate(string? text, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var entry = EntryOf(line.TrimEnd('\r'));
            if (entry is { } item && values.ContainsKey(item.Key) && !found.ContainsKey(item.Key))
            {
                found[item.Key] = item.Value;
            }
        }

        return values.All(pair => found.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    /// <summary>
    /// Writes the file (the loopback form) unless it already holds exactly these values; the
    /// previous version goes to a `.pnr-bak-&lt;timestamp&gt;` copy, at most three are kept.
    /// </summary>
    public static (bool Written, string? BackupPath) Write(AppConfig config)
    {
        var values = Values(config);
        var path = config.Guard.CodexEnvPath;
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var updated = Apply(existing, values);
        if (existing == updated)
        {
            ClaudeSettingsGuard.PruneBackups(path);
            return (false, null);
        }

        var backup = ClaudeSettingsGuard.Backup(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
        ClaudeSettingsGuard.PruneBackups(path);
        return (true, backup);
    }

    /// <summary>Key of a `KEY=value` or `export KEY=value` line; null — a comment or not a key line.</summary>
    internal static string? KeyOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["export ".Length..].TrimStart();
        }

        return EntryOf(trimmed)?.Key;
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
