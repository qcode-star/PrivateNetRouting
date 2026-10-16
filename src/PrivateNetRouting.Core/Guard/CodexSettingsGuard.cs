using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// The persistent guard layer of Codex (ChatGPT desktop and the CLI), two files of CODEX_HOME:
/// the flag `[features] respect_system_proxy = true` in `~/.codex/config.toml` — a belt-and-braces
/// for the guard's WinINET system-proxy layer (upstream the flag is not reliably honoured:
/// openai/codex#39237, #29958 — connectivity never rests on it) — and the proxy env
/// (HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/NO_PROXY) in `~/.codex/.env`.
/// The embedded CLI core loads .env after its own start: the desktop app strips the inherited
/// proxy env variables from the processes it spawns, so this file is what actually carries the
/// proxy to the core (the launcher's env reaches only the Chromium part, via the flags).
///
/// The desktop app owns config.toml and rewrites it, so the edit is surgical: every other line is
/// preserved byte for byte, only the flag line is inserted or rewritten, the newline style is
/// kept. The .env merge is the same: our keys' lines are rewritten, foreign lines survive. Before
/// a rewrite the previous version is preserved unless an identical copy is already among the
/// `.pnr-bak-*` backups (the matching copy is touched; three numbered slots, at most three
/// such copies are kept per file). Requires no special rights.
/// </summary>
public sealed class CodexSettingsGuard(AppConfig config)
{
    private const string SectionName = "features";
    private const string KeyName = "respect_system_proxy";
    private const string DottedKey = SectionName + "." + KeyName;

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
            if (value == true && envOk)
            {
                return new CodexSettingsStatus(GuardState.On, true, FileMissing: false, EnvUpToDate: true);
            }

            var parts = new List<string>();
            if (flagText is null)
            {
                parts.Add("config.toml not found");
            }
            else
            {
                parts.Add(value switch
                {
                    false => $"{KeyName} = false (expected true)",
                    _ => $"{DottedKey} is not set",
                });
            }

            if (!envOk)
            {
                parts.Add(envText is null ? ".env not found" : ".env proxy env stale");
            }

            return new CodexSettingsStatus(GuardState.Partial, value, !flagExists, envOk)
            {
                Detail = string.Join("; ", parts),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodexSettingsStatus(GuardState.Unknown, null, FileMissing: false, EnvUpToDate: false) { Detail = ex.Message };
        }
    }

    /// <summary>Outcome of a sync: which of the two files were rewritten and which backups were made.</summary>
    public sealed record CodexSettingsSyncResult(bool Written, string? BackupPath, bool EnvWritten, string? EnvBackupPath);

    /// <summary>Writes the flag and the .env proxy env; each file is touched only on an actual change.</summary>
    public CodexSettingsSyncResult Sync()
    {
        var path = config.Codex.SettingsPath;
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var updated = Apply(existing);
        string? backup = null;
        var written = existing != updated;
        if (written)
        {
            backup = ClaudeSettingsGuard.Backup(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, updated);
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
        var flagLine = $"{KeyName} = true";
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
            lines.Insert(AfterValue(StructuralLines(lines), location.DottedSiblingIndex), $"{DottedKey} = true");
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

    /// <summary>`features = { ... }` with the flag set to true: an existing value is rewritten, otherwise the key leads the table.</summary>
    private static string SetInlineFlag(string line)
    {
        if (InlineFlag.IsMatch(line))
        {
            return InlineFlag.Replace(line, $"{KeyName} = true", 1);
        }

        var brace = line.IndexOf('{', line.IndexOf('=') + 1);
        var rest = line[(brace + 1)..].TrimStart();
        var insertion = rest.StartsWith('}') ? $" {KeyName} = true " : $" {KeyName} = true, ";
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
    /// The line with its value replaced by `true`; the key spelling, the spacing around '=' and
    /// an inline comment are kept (the desktop app's own style stays recognizable).
    /// </summary>
    private static string ReplaceValue(string line)
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

        return $"{line[..eq].TrimEnd()} = true{comment}";
    }
}
