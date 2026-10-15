using System.Text;

namespace PrivateNetRouting.Core.Guard;

public sealed record HostsBlockState(bool Present, int EntryCount, bool Current);

/// <summary>
/// Managed block in hosts: the block is rebuilt from the list,
/// so re-applying updates it; foreign lines are left alone. Writing to the system hosts
/// file requires administrator rights (performed in the elevated helper).
/// </summary>
public static class HostsFileBlock
{
    /// <summary>
    /// Reads the state of the block: whether the markers are present, how many "0.0.0.0 name"
    /// entries are inside (counting only lines starting with "0.0.0.0 "),
    /// and whether the content matches the desired list. Read rights suffice for any user.
    /// </summary>
    public static HostsBlockState Read(string path, string begin, string end, string comment, IReadOnlyList<string> domains)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return new HostsBlockState(false, 0, false);
        }

        if (IsUnterminated(lines, begin, end))
        {
            // the begin marker is there but the block has no end: its extent is unknown, so it is
            // reported present and not current, with no entries counted
            return new HostsBlockState(true, 0, false);
        }

        var inside = false;
        var block = new List<string>();
        var present = false;
        foreach (var line in lines)
        {
            if (line == begin)
            {
                inside = true;
                present = true;
                block.Add(line);
                continue;
            }

            if (line == end)
            {
                inside = false;
                block.Add(line);
                continue;
            }

            if (inside)
            {
                block.Add(line);
            }
        }

        var desired = BuildLines(begin, end, domains, comment);
        return new HostsBlockState(
            present,
            block.Count(line => line.StartsWith("0.0.0.0 ", StringComparison.Ordinal)),
            string.Join("\n", block) == string.Join("\n", desired));
    }

    /// <summary>Writes (or updates) the block; if already current, the file is left untouched.</summary>
    public static void Apply(string path, string begin, string end, string comment, IReadOnlyList<string> domains)
    {
        var lines = File.ReadAllLines(path);
        ThrowIfUnterminated(lines, begin, end);
        var (kept, current) = Split(lines, begin, end);
        var desired = BuildLines(begin, end, domains, comment);
        if (string.Join("\n", current) == string.Join("\n", desired))
        {
            return;
        }

        WriteAll(path, [.. kept, "", .. desired, ""]);
    }

    /// <summary>Removes the block; if absent, the file is left untouched (but trailing blank lines are not trimmed).</summary>
    public static void Remove(string path, string begin, string end)
    {
        var lines = File.ReadAllLines(path);
        ThrowIfUnterminated(lines, begin, end);
        var (kept, current) = Split(lines, begin, end);
        if (current.Count == 0)
        {
            return;
        }

        WriteAll(path, kept);
    }

    /// <summary>Whether a begin marker is followed by no end marker (a hand-edited or changed-marker file).</summary>
    private static bool IsUnterminated(string[] lines, string begin, string end)
    {
        var inside = false;
        foreach (var line in lines)
        {
            if (line == begin)
            {
                inside = true;
            }
            else if (line == end)
            {
                inside = false;
            }
        }

        return inside;
    }

    /// <summary>Refuses to rewrite a file whose block has no end marker: everything after it would be swallowed.</summary>
    private static void ThrowIfUnterminated(string[] lines, string begin, string end)
    {
        if (IsUnterminated(lines, begin, end))
        {
            throw new IOException(
                $"hosts: the block begin marker '{begin}' has no matching end marker '{end}'; "
                + "fix the file by hand (or the configured markers) — refusing to rewrite it");
        }
    }

    /// <summary>Splits the file: lines outside the block (trailing blanks trimmed) and inside it.</summary>
    private static (List<string> Kept, List<string> Block) Split(string[] lines, string begin, string end)
    {
        var kept = new List<string>();
        var block = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line == begin)
            {
                inside = true;
                block.Add(line);
                continue;
            }

            if (line == end)
            {
                inside = false;
                block.Add(line);
                continue;
            }

            if (inside)
            {
                block.Add(line);
            }
            else
            {
                kept.Add(line);
            }
        }

        while (kept.Count > 0 && kept[^1] == "")
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return (kept, block);
    }

    private static List<string> BuildLines(string begin, string end, IReadOnlyList<string> domains, string? comment)
    {
        var lines = new List<string> { begin };
        if (!string.IsNullOrEmpty(comment))
        {
            lines.Add(comment);
        }

        lines.AddRange(domains.Select(domain => $"0.0.0.0 {domain}"));
        lines.Add(end);
        return lines;
    }

    /// <summary>ASCII encoding and CRLF (hosts dislikes anything else).</summary>
    private static void WriteAll(string path, List<string> lines) =>
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
}
