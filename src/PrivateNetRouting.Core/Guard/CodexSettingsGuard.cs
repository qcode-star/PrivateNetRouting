using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// The persistent guard layer of Codex (ChatGPT desktop and the CLI), two files of CODEX_HOME:
/// the flag `[features] respect_system_proxy = false` and the `[features.network_proxy]` block in
/// `~/.codex/config.toml` — the latter turns on Codex's managed network proxy for sandboxed
/// commands and lets it honour the proxy env for its upstream, so command traffic leaves through
/// the application's forward proxy instead of a direct route. The permission profile still decides
/// whether the sandbox has network at all; the guard never edits `[permissions]` or
/// `default_permissions`. The second file is the proxy env
/// (HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/NO_PROXY/NODE_USE_ENV_PROXY) in `~/.codex/.env`.
/// The embedded CLI core loads .env after its own start: the desktop app strips the inherited
/// proxy env variables from the processes it spawns, so this file is what actually carries the
/// proxy to the core (the launcher's env reaches only the Chromium part, via the flags).
///
/// The flag is maintained off on purpose. With `respect_system_proxy = true` Codex resolves the
/// system proxy first; when the system proxy is disabled the route resolves to direct and the
/// client calls `no_proxy()`, so the proxy env of .env is ignored — login/token exchange and
/// account requests then go direct and fail on a blocked network. With `false` the core keeps
/// reqwest's default proxy behavior, which reads HTTP_PROXY/HTTPS_PROXY/ALL_PROXY from .env; that
/// is the transport this guard relies on.
///
/// The same file carries the privacy policy: `[analytics] enabled = false` (the desktop shell keys
/// OpenAI analytics, Sentry error reporting, website reporting and its Datadog sink off the same
/// flag; the core's Statsig metrics exporter follows it), `[feedback] enabled = false` (no feedback
/// uploads, no background Guardian review retention) and the top-level
/// `check_for_update_on_startup = false` (the startup update check; the desktop app itself updates
/// through the Microsoft Store). These are the guard's policy, not user preferences: every sync
/// restores them. Codex's own commit/PR attribution is deliberately not among them — it is a
/// server-side account/workspace setting (`commit_attribution_enabled`), not a config.toml key.
///
/// The desktop app owns config.toml and rewrites it, so the edits are surgical: every other line is
/// preserved byte for byte, only the maintained keys are inserted or rewritten, the newline style
/// is kept. A `features = { … }` inline table or dotted `features.network_proxy.*` keys are a shape
/// this editor refuses instead of rewriting (InvalidDataException). The .env merge is the same:
/// our keys' lines are rewritten, foreign lines survive. Before a rewrite the previous version is
/// preserved unless an identical copy is already among the `.pnr-bak-*` backups (the matching copy
/// is touched; three numbered slots, at most three such copies are kept per file). Requires no
/// special rights.
/// </summary>
public sealed class CodexSettingsGuard(AppConfig config)
{
    private const string SectionName = "features";
    private const string KeyName = "respect_system_proxy";
    private const string DottedKey = SectionName + "." + KeyName;
    private const string FlagValue = "false";

    // The sandbox network layer of newer Codex builds: Codex runs its own managed proxy for the
    // commands a permission profile lets out to the network, and that proxy honours the proxy env
    // (HTTP_PROXY/HTTPS_PROXY/ALL_PROXY) for its upstream. The table below turns the proxy on and
    // allows every host, so command traffic is not filtered here — it is only forced through the
    // chain. The permission profile keeps deciding whether the sandbox has network at all; this
    // guard deliberately does not touch default_permissions or any [permissions] table.
    private const string NetworkSection = "features.network_proxy";
    private const string DomainsSection = "features.network_proxy.domains";
    private static readonly (string Key, string Value)[] NetworkKeys =
    [
        ("enabled", "true"),
        ("allow_upstream_proxy", "true"),
        ("enable_socks5", "false"),
    ];

    private static readonly (string Key, string Value)[] DomainKeys = [("\"*\"", "\"allow\"")];

    // The privacy policy: OpenAI analytics (the desktop shell also keys Sentry error reporting,
    // website reporting and its Datadog sink off the same flag), feedback uploads, and the
    // startup update check. `analytics` and `feedback` are one-key tables; the check is top-level.
    private static readonly (string Section, (string Key, string Value)[] Keys)[] PrivacySections =
    [
        ("analytics", [("enabled", "false")]),
        ("feedback", [("enabled", "false")]),
    ];

    private const string UpdateCheckKey = "check_for_update_on_startup";
    private const string UpdateCheckValue = "false";

    public CodexSettingsStatus Read()
    {
        var path = config.Codex.SettingsPath;
        var envPath = config.Codex.EnvPath;
        try
        {
            var flagExists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            var envExists = !string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath);
            var flagText = flagExists ? File.ReadAllText(path) : null;
            var envText = envExists ? File.ReadAllText(envPath) : null;
            var envOk = CodexEnvFile.UpToDate(envText, CodexEnvFile.Values(config));
            if (!flagExists && !envExists)
            {
                // both files absent: Codex is not installed or has never run
                return new CodexSettingsStatus(GuardState.Off, null, FileMissing: true, EnvUpToDate: false);
            }

            var value = FindValue(flagText);
            var networkOk = NetworkProxyUpToDate(flagText);
            var privacyOk = PrivacyUpToDate(flagText);
            if (value == false && networkOk && privacyOk && envOk)
            {
                return new CodexSettingsStatus(GuardState.On, false, FileMissing: false, EnvUpToDate: true)
                {
                    NetworkProxyUpToDate = true,
                    PrivacyUpToDate = true,
                };
            }

            var parts = new List<string>();
            if (flagText is null)
            {
                parts.Add("config.toml not found");
            }
            else if (value != false)
            {
                parts.Add(value switch
                {
                    true => $"{KeyName} = true (expected false)",
                    _ => $"{DottedKey} is not set",
                });
            }

            if (!networkOk)
            {
                parts.Add($"{NetworkSection} is not enabled (expected enabled = true, allow_upstream_proxy = true, enable_socks5 = false, domains \"*\" = allow)");
            }

            if (!privacyOk)
            {
                parts.Add("the privacy switches are not set (expected analytics.enabled = false, feedback.enabled = false, check_for_update_on_startup = false)");
            }

            if (!envOk)
            {
                parts.Add(envText is null ? ".env not found" : ".env proxy env stale");
            }

            return new CodexSettingsStatus(GuardState.Partial, value, !flagExists, envOk)
            {
                Detail = string.Join("; ", parts),
                NetworkProxyUpToDate = networkOk,
                PrivacyUpToDate = privacyOk,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodexSettingsStatus(GuardState.Unknown, null, FileMissing: false, EnvUpToDate: false) { Detail = ex.Message };
        }
    }

    /// <summary>Outcome of a sync: which of the two files were rewritten and which backups were made.</summary>
    public sealed record CodexSettingsSyncResult(bool Written, string? BackupPath, bool EnvWritten, string? EnvBackupPath);

    /// <summary>Writes the flag, the network-proxy block and the .env proxy env; each file is touched only on an actual change.</summary>
    public CodexSettingsSyncResult Sync()
    {
        var path = config.Codex.SettingsPath;
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var updated = ApplyPrivacy(ApplyNetworkProxy(Apply(existing)));
        string? backup = null;
        var written = existing != updated;
        if (written)
        {
            backup = ClaudeSettingsGuard.Backup(path);
            AtomicFile.Write(path, updated);
        }

        // after the new copy exists, so "at most three" counts it
        ClaudeSettingsGuard.PruneBackups(path);

        var (envWritten, envBackup) = CodexEnvFile.Write(config);
        return new CodexSettingsSyncResult(written, backup, envWritten, envBackup);
    }

    /// <summary>Value of the flag from a config.toml text; null — absent or unparsable.</summary>
    internal static bool? FindValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Locate(SplitLines(text)).Value;
    }

    /// <summary>Whether a config.toml carries the maintained Codex shape: the flag off, the managed-proxy block and the privacy switches.</summary>
    internal static bool ConfigUpToDate(string? text) =>
        FindValue(text) == false && NetworkProxyUpToDate(text) && PrivacyUpToDate(text);

    /// <summary>
    /// The flag line inserted/updated in a config.toml text: an existing key is rewritten in place
    /// (both the section form and the top-level dotted form), otherwise the key is inserted into
    /// the existing `[features]` section or a new section is appended. null/empty input — a fresh
    /// file with just the section.
    /// </summary>
    internal static string Apply(string? existing)
    {
        var newline = existing is not null && existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(existing ?? "");
        var flagLine = $"{KeyName} = {FlagValue}";
        var location = Locate(lines);

        if (location.KeyIndex >= 0)
        {
            lines[location.KeyIndex] = ReplaceValue(lines[location.KeyIndex]);
        }
        else if (location.InlineIndex >= 0)
        {
            // features = { ... } is the table already: a second [features] would be invalid TOML
            lines[location.InlineIndex] = SetInlineFlag(lines[location.InlineIndex]);
        }
        else if (location.SectionStart >= 0)
        {
            lines.Insert(location.SectionStart + 1, flagLine);
        }
        else if (location.DottedSiblingIndex >= 0)
        {
            // features.other = ... already defines the table by dotted keys; the flag joins them,
            // after the sibling's complete value (a multi-line array or string must not be split)
            lines.Insert(AfterValue(StructuralLines(lines), location.DottedSiblingIndex), $"{DottedKey} = {FlagValue}");
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{SectionName}]");
            lines.Add(flagLine);
        }

        return string.Join(newline, lines) + newline;
    }

    /// <summary>
    /// Whether config.toml carries the maintained managed-proxy block: the three keys of
    /// `[features.network_proxy]` and the allow-everything entry of
    /// `[features.network_proxy.domains]`. The permission profile is not inspected: whether the
    /// block actually starts a proxy is the app's network toggle, not this file's business.
    /// </summary>
    internal static bool NetworkProxyUpToDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lines = SplitLines(text);
        var structural = StructuralLines(lines);
        var network = ReadSectionKeys(lines, structural, NetworkSection);
        if (network is null || !MaintainedKeysMatch(network, NetworkKeys))
        {
            return false;
        }

        var domains = ReadSectionKeys(lines, structural, DomainsSection);
        return domains is not null && MaintainedKeysMatch(domains, DomainKeys);
    }

    /// <summary>
    /// Whether config.toml carries the maintained privacy switches: `analytics.enabled = false`,
    /// `feedback.enabled = false` and the top-level `check_for_update_on_startup = false`.
    /// </summary>
    internal static bool PrivacyUpToDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lines = SplitLines(text);
        var structural = StructuralLines(lines);
        foreach (var (section, keys) in PrivacySections)
        {
            var actual = ReadSectionKeys(lines, structural, section);
            // exact booleans: a quoted "false" is a wrong type, not the maintained value
            if (actual is null || keys.Any(key => !actual.TryGetValue(NormalizeKey(key.Key), out var value) || value != key.Value))
            {
                return false;
            }
        }

        return FindRootKey(lines, structural, UpdateCheckKey) is { Value: UpdateCheckValue };
    }

    /// <summary>
    /// The managed-proxy block inserted/updated in a config.toml text: the maintained keys are
    /// rewritten in place or inserted after the section header, foreign keys and every other line
    /// survive. A shape this editor cannot edit surgically — the `features` inline table or dotted
    /// `features.network_proxy.*` keys — is refused with InvalidDataException rather than rewritten.
    /// An already-correct text is returned unchanged (idempotence).
    /// </summary>
    internal static string ApplyNetworkProxy(string? existing)
    {
        if (existing is not null && NetworkProxyUpToDate(existing))
        {
            return existing;
        }

        var newline = existing is not null && existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(existing ?? "");
        RefuseUnsupportedNetworkShapes(lines);

        var structural = StructuralLines(lines);
        var network = FindSection(lines, structural, NetworkSection);
        if (network is null)
        {
            var domains = FindSection(lines, structural, DomainsSection);
            var block = new List<string>();
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                block.Add("");
            }

            block.Add($"[{NetworkSection}]");
            block.AddRange(NetworkKeys.Select(key => $"{key.Key} = {key.Value}"));
            if (domains is { } bounds)
            {
                // the sub-table already exists and implicitly defines the parent: the parent
                // header and its keys go right before it, not at the end of the file
                lines.InsertRange(bounds.Header, block);
            }
            else
            {
                lines.AddRange(block);
            }
        }

        structural = StructuralLines(lines);
        var section = FindSection(lines, structural, NetworkSection)
            ?? throw new InvalidDataException($"could not locate [{NetworkSection}] after adding it to config.toml");
        SetSectionKeys(lines, structural, section.Header, section.End, NetworkKeys);

        structural = StructuralLines(lines);
        var domainSection = FindSection(lines, structural, DomainsSection);
        if (domainSection is null)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{DomainsSection}]");
            lines.AddRange(DomainKeys.Select(key => $"{key.Key} = {key.Value}"));
        }
        else
        {
            SetSectionKeys(lines, structural, domainSection.Value.Header, domainSection.Value.End, DomainKeys);
        }

        return string.Join(newline, lines) + newline;
    }

    /// <summary>
    /// The privacy switches inserted/updated in a config.toml text: `[analytics] enabled = false`,
    /// `[feedback] enabled = false` and the top-level `check_for_update_on_startup = false` (the key
    /// goes before the first table header — after it, it would belong to that table). Foreign keys
    /// and every other line survive; the newline style is kept. A shape this editor cannot edit
    /// surgically — an inline `analytics = { … }` / `feedback = { … }` table or dotted
    /// `analytics.*` / `feedback.*` keys — is refused with InvalidDataException. An already-correct
    /// text is returned unchanged (idempotence).
    /// </summary>
    internal static string ApplyPrivacy(string? existing)
    {
        if (existing is not null && PrivacyUpToDate(existing))
        {
            return existing;
        }

        var newline = existing is not null && existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(existing ?? "");
        RefuseUnsupportedPrivacyShapes(lines);

        foreach (var (section, keys) in PrivacySections)
        {
            var structural = StructuralLines(lines);
            if (FindSection(lines, structural, section) is { } bounds)
            {
                SetSectionKeys(lines, structural, bounds.Header, bounds.End, keys);
                continue;
            }

            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{section}]");
            lines.AddRange(keys.Select(key => $"{key.Key} = {key.Value}"));
        }

        var root = StructuralLines(lines);
        if (FindRootKey(lines, root, UpdateCheckKey) is { } found)
        {
            lines[found.Index] = ReplaceValueWith(lines[found.Index], UpdateCheckValue);
        }
        else
        {
            var update = $"{UpdateCheckKey} = {UpdateCheckValue}";
            var header = FirstHeader(lines, root);
            if (header >= 0)
            {
                // a top-level key must stay above the first table header; one blank line keeps
                // the key visually separate from the table
                lines.InsertRange(header, [update, ""]);
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                {
                    lines.Add("");
                }

                lines.Add(update);
            }
        }

        return string.Join(newline, lines) + newline;
    }

    /// <summary>Keys of a section, normalized and with the value's inline comment stripped; null — no such section.</summary>
    private static Dictionary<string, string>? ReadSectionKeys(IReadOnlyList<string> lines, bool[] structural, string name)
    {
        if (FindSection(lines, structural, name) is not { } bounds)
        {
            return null;
        }

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = bounds.Header + 1; i < bounds.End; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            var key = KeyOf(trimmed);
            if (key is not null && !keys.ContainsKey(key))
            {
                keys[key] = ValueOf(trimmed);
            }
        }

        return keys;
    }

    /// <summary>The section header and the line where it ends (the next header or the end of the file).</summary>
    private static (int Header, int End)? FindSection(IReadOnlyList<string> lines, bool[] structural, string name)
    {
        var header = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith('[') || trimmed.IndexOf(']') <= 1)
            {
                continue;
            }

            if (header < 0)
            {
                if (HeaderName(trimmed) == name)
                {
                    header = i;
                }
            }
            else
            {
                return (header, i);
            }
        }

        return header < 0 ? null : (header, lines.Count);
    }

    /// <summary>
    /// The top-level key and its value; null when the key is absent from the root region. A key
    /// after the first table header belongs to that table, not to the root.
    /// </summary>
    private static (int Index, string Value)? FindRootKey(IReadOnlyList<string> lines, bool[] structural, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.IndexOf(']') > 1)
            {
                return null;
            }

            if (KeyOf(trimmed) == key)
            {
                return (i, ValueOf(trimmed));
            }
        }

        return null;
    }

    /// <summary>The first table header; -1 — the file has no table header.</summary>
    private static int FirstHeader(IReadOnlyList<string> lines, bool[] structural)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.IndexOf(']') > 1)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Rewrites the maintained keys of a section, inserting the missing ones right after its
    /// header in the declared order; foreign keys of the section stay where they are.
    /// </summary>
    private static void SetSectionKeys(
        List<string> lines,
        bool[] structural,
        int header,
        int end,
        IReadOnlyList<(string Key, string Value)> keys)
    {
        var existing = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = header + 1; i < end; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var key = KeyOf(lines[i].Trim());
            if (key is not null && !existing.ContainsKey(key))
            {
                existing[key] = i;
            }
        }

        var missing = new List<(string Key, string Value)>();
        foreach (var (key, value) in keys)
        {
            if (existing.TryGetValue(NormalizeKey(key), out var index))
            {
                lines[index] = ReplaceValueWith(lines[index], value);
            }
            else
            {
                missing.Add((key, value));
            }
        }

        for (var i = missing.Count - 1; i >= 0; i--)
        {
            lines.Insert(header + 1, $"{missing[i].Key} = {missing[i].Value}");
        }
    }

    /// <summary>
    /// `features = { … }` and top-level `features.network_proxy.*` keys define the same table in a
    /// shape the line editor cannot extend without rewriting it: refuse instead of guessing.
    /// </summary>
    private static void RefuseUnsupportedNetworkShapes(IReadOnlyList<string> lines)
    {
        var structural = StructuralLines(lines);
        var current = "";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.IndexOf(']') > 1)
            {
                current = HeaderName(trimmed);
                continue;
            }

            if (current.Length != 0)
            {
                continue;
            }

            var key = KeyOf(trimmed);
            if (key is null)
            {
                continue;
            }

            if (key == SectionName && ValueOf(trimmed).StartsWith('{'))
            {
                throw new InvalidDataException(
                    $"{SectionName} is an inline table in config.toml; the {NetworkSection} block cannot be added without rewriting it");
            }

            if (key == NetworkSection || key.StartsWith(NetworkSection + ".", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{NetworkSection} is defined by dotted keys in config.toml; the block cannot be added without rewriting it");
            }
        }
    }

    /// <summary>
    /// A root `analytics = { … }` / `feedback = { … }` inline table or dotted `analytics.*` /
    /// `feedback.*` keys define the same table in a shape the line editor cannot extend without
    /// rewriting it: refuse instead of guessing.
    /// </summary>
    private static void RefuseUnsupportedPrivacyShapes(IReadOnlyList<string> lines)
    {
        var structural = StructuralLines(lines);
        var current = "";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.IndexOf(']') > 1)
            {
                current = HeaderName(trimmed);
                continue;
            }

            if (current.Length != 0)
            {
                continue;
            }

            var key = KeyOf(trimmed);
            if (key is null)
            {
                continue;
            }

            foreach (var (section, _) in PrivacySections)
            {
                if (key == section)
                {
                    throw new InvalidDataException(
                        $"{section} is not a section table in config.toml; the privacy switch cannot be added without rewriting it");
                }

                if (key.StartsWith(section + ".", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{section} is defined by dotted keys in config.toml; the privacy switch cannot be added without rewriting it");
                }
            }
        }
    }

    private static bool MaintainedKeysMatch(
        IReadOnlyDictionary<string, string> actual,
        IReadOnlyList<(string Key, string Value)> expected)
    {
        foreach (var (key, value) in expected)
        {
            if (!actual.TryGetValue(NormalizeKey(key), out var current) || Unquote(current) != Unquote(value))
            {
                return false;
            }
        }

        return true;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '"' or '\''
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>
    /// Splits a TOML text into lines without terminators, dropping the empty tail of a trailing
    /// newline (a text without one is written back with a terminator — the usual TOML style).
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        var lines = text.Length == 0
            ? []
            : text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Where the flag lives: the key line (if any) and the bounds of the [features] table.</summary>
    private readonly record struct FlagLocation(
        int KeyIndex,
        bool Dotted,
        bool? Value,
        int SectionStart,
        int SectionEnd,
        int InlineIndex = -1,
        int DottedSiblingIndex = -1);

    // the whole key, bare or quoted: not the tail of another bare key (no_respect_system_proxy)
    // or of a dotted one (x.respect_system_proxy)
    private static readonly System.Text.RegularExpressions.Regex InlineFlag =
        new($@"(?<![\w.\-""'])([""']?){KeyName}\1\s*=\s*(true|false)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static FlagLocation Locate(IReadOnlyList<string> lines)
    {
        // lines inside a multi-line value are not structure: a section header or a key there means nothing
        var structural = StructuralLines(lines);
        var sectionStart = -1;
        var sectionEnd = -1;
        var current = "";
        var inlineIndex = -1;
        bool? inlineValue = null;
        var siblingIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!structural[i])
            {
                continue;
            }

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.IndexOf(']') > 1)
            {
                var name = HeaderName(trimmed);
                if (sectionStart >= 0 && sectionEnd < 0)
                {
                    sectionEnd = i;
                }

                current = name;
                if (current == SectionName && sectionStart < 0)
                {
                    sectionStart = i;
                }

                continue;
            }

            var key = KeyOf(trimmed);
            if (key is null)
            {
                continue;
            }

            var dotted = current.Length == 0 && key == DottedKey;
            if (dotted || (current == SectionName && key == KeyName))
            {
                return new FlagLocation(i, dotted, ParseBool(ValueOf(trimmed)), sectionStart, sectionEnd < 0 ? lines.Count : sectionEnd);
            }

            if (current.Length == 0 && inlineIndex < 0 && key == SectionName && ValueOf(trimmed).StartsWith('{'))
            {
                inlineIndex = i;
                var match = InlineFlag.Match(ValueOf(trimmed));
                inlineValue = match.Success ? ParseBool(match.Groups[2].Value) : null;
            }
            else if (current.Length == 0 && siblingIndex < 0 && key.StartsWith(SectionName + ".", StringComparison.Ordinal))
            {
                siblingIndex = i;
            }
        }

        return new FlagLocation(-1, false, inlineValue, sectionStart, sectionEnd < 0 ? lines.Count : sectionEnd, inlineIndex, siblingIndex);
    }

    /// <summary>
    /// Marks the structural lines: those that start outside any value — not inside a multi-line
    /// basic (""") or literal (''') string and not inside a multi-line array or inline table.
    /// Only such a line can be a table header or begin a key; the rest continue the value of the
    /// key above them (a section header or a key there means nothing).
    /// </summary>
    private static bool[] StructuralLines(IReadOnlyList<string> lines)
    {
        var structural = new bool[lines.Count];
        var openString = ""; // the delimiter of an open multi-line string
        var depth = 0; // open [ and { of a value
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            structural[i] = openString.Length == 0 && depth == 0;
            var position = 0;
            if (structural[i] && line.TrimStart().StartsWith('['))
            {
                // a table header: its brackets are not a value
                continue;
            }

            while (position < line.Length)
            {
                if (openString.Length > 0)
                {
                    var close = FindClosing(line, position, openString);
                    if (close < 0)
                    {
                        break;
                    }

                    position = close + openString.Length;
                    openString = "";
                    continue;
                }

                var c = line[position];
                if (c == '#')
                {
                    break;
                }

                if (line.AsSpan(position).StartsWith("\"\"\"") || line.AsSpan(position).StartsWith("'''"))
                {
                    openString = line.Substring(position, 3);
                    position += 3;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    var close = FindClosing(line, position + 1, c.ToString());
                    // an unterminated one-line string is invalid TOML: the rest of the line is its text
                    position = close < 0 ? line.Length : close + 1;
                    continue;
                }

                if (c is '[' or '{')
                {
                    depth++;
                }
                else if ((c is ']' or '}') && depth > 0)
                {
                    depth--;
                }

                position++;
            }
        }

        return structural;
    }

    /// <summary>The index of the closing delimiter at or after start; a backslash escapes in basic strings only.</summary>
    private static int FindClosing(string line, int start, string delimiter)
    {
        var escapes = delimiter[0] == '"';
        for (var i = start; i <= line.Length - delimiter.Length; i++)
        {
            if (escapes && line[i] == '\\')
            {
                i++;
                continue;
            }

            if (string.CompareOrdinal(line, i, delimiter, 0, delimiter.Length) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The first line after the value that begins at keyLine: the next structural line (or the end).</summary>
    private static int AfterValue(bool[] structural, int keyLine)
    {
        var next = keyLine + 1;
        while (next < structural.Length && !structural[next])
        {
            next++;
        }

        return next;
    }

    /// <summary>"[features]" → "features"; "[[x]]" → "x"; dotted and quoted parts are normalized.</summary>
    private static string HeaderName(string trimmed)
    {
        var name = StripComment(trimmed).TrimStart('[').TrimEnd(']').Trim();
        return NormalizeKey(name);
    }

    /// <summary>Key part of a "key = value" line, dotted and quoted parts normalized; null — not a key line.</summary>
    private static string? KeyOf(string trimmed)
    {
        var eq = trimmed.IndexOf('=');
        if (eq <= 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        var key = trimmed[..eq].Trim();
        return key.Length == 0 ? null : NormalizeKey(key);
    }

    /// <summary>The line without its trailing # comment (a # inside a quoted string stays).</summary>
    private static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '#')
            {
                return line[..i].TrimEnd();
            }

            if (line[i] is '"' or '\'')
            {
                var end = line.IndexOf(line[i], i + 1);
                if (end < 0)
                {
                    break;
                }

                i = end;
            }
        }

        return line;
    }

    /// <summary>`features = { ... }` with the flag set to false: an existing value is rewritten, otherwise the key leads the table.</summary>
    private static string SetInlineFlag(string line)
    {
        if (InlineFlag.IsMatch(line))
        {
            return InlineFlag.Replace(line, $"{KeyName} = {FlagValue}", 1);
        }

        var brace = line.IndexOf('{', line.IndexOf('=') + 1);
        var rest = line[(brace + 1)..].TrimStart();
        var insertion = rest.StartsWith('}') ? $" {KeyName} = {FlagValue} " : $" {KeyName} = {FlagValue}, ";
        return line[..(brace + 1)] + insertion + line[(brace + 1)..].TrimStart();
    }

    private static string NormalizeKey(string key) =>
        string.Join('.', key.Split('.').Select(part => part.Trim().Trim('\'', '"')));

    private static string ValueOf(string trimmed)
    {
        var eq = trimmed.IndexOf('=');
        var value = trimmed[(eq + 1)..];
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '#')
            {
                return value[..i].Trim();
            }

            if (value[i] is '"' or '\'')
            {
                var quote = value[i];
                var end = value.IndexOf(quote, i + 1);
                if (end < 0)
                {
                    break;
                }

                i = end;
            }
        }

        return value.Trim();
    }

    private static bool? ParseBool(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>
    /// The line with its value replaced; the key spelling, the spacing around '=' and an inline
    /// comment are kept (the desktop app's own style stays recognizable).
    /// </summary>
    private static string ReplaceValueWith(string line, string value)
    {
        var eq = line.IndexOf('=');
        var tail = line[(eq + 1)..];
        var comment = "";
        for (var i = 0; i < tail.Length; i++)
        {
            if (tail[i] == '#')
            {
                comment = "  " + tail[i..].Trim();
                break;
            }

            if (tail[i] is '"' or '\'')
            {
                var quote = tail[i];
                var end = tail.IndexOf(quote, i + 1);
                if (end < 0)
                {
                    break;
                }

                i = end;
            }
        }

        return $"{line[..eq].TrimEnd()} = {value}{comment}";
    }

    /// <summary>
    /// The line with its value replaced by `false`; the key spelling, the spacing around '=' and
    /// an inline comment are kept (the desktop app's own style stays recognizable).
    /// </summary>
    private static string ReplaceValue(string line) => ReplaceValueWith(line, FlagValue);
}
