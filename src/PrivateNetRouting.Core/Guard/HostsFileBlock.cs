using System.Text;

namespace PrivateNetRouting.Core.Guard;

public sealed record HostsBlockState(bool Present, int EntryCount, bool Current);

/// <summary>
/// Управляемый блок в hosts (порт ban-ai-hosts.ps1): блок пересобирается из списка,
/// поэтому повторное применение обновляет его; чужие строки не трогаются. Запись в
/// системный hosts требует прав администратора (выполняется в элевейтнутом helper).
/// </summary>
public static class HostsFileBlock
{
    /// <summary>
    /// Читает состояние блока: есть ли маркеры, сколько записей «0.0.0.0 имя» внутри
    /// (счёт — как в прежней схеме: только строки, начинающиеся с «0.0.0.0 »), и совпадает
    /// ли содержимое с желаемым списком. Прав на чтение хватает любому пользователю.
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

    /// <summary>Записывает (или обновляет) блок; уже актуален — файл не трогается.</summary>
    public static void Apply(string path, string begin, string end, string comment, IReadOnlyList<string> domains)
    {
        var (kept, current) = Split(File.ReadAllLines(path), begin, end);
        var desired = BuildLines(begin, end, domains, comment);
        if (string.Join("\n", current) == string.Join("\n", desired))
        {
            return;
        }

        WriteAll(path, [.. kept, "", .. desired, ""]);
    }

    /// <summary>Снимает блок; при его отсутствии файл не трогается (но хвостовые пустые строки не подрезаются).</summary>
    public static void Remove(string path, string begin, string end)
    {
        var (kept, current) = Split(File.ReadAllLines(path), begin, end);
        if (current.Count == 0)
        {
            return;
        }

        WriteAll(path, kept);
    }

    /// <summary>Разделение файла: строки вне блока (хвостовые пустые срезаются) и внутри него.</summary>
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

    /// <summary>Кодировка ASCII и CRLF — как Set-Content в прежней схеме (hosts не любит другое).</summary>
    private static void WriteAll(string path, List<string> lines) =>
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
}
