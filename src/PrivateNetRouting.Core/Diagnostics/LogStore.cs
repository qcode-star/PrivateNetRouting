using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Diagnostics;

public sealed record LogFileInfo(string Name, long Bytes, DateTime LastWriteTime);

public sealed record LogTail(IReadOnlyList<string> Lines, long TotalBytes);

public sealed record LogRotationResult(string Name, bool Ok, string Detail)
{
    public static LogRotationResult Done(string name) => new(name, true, "rotated");

    public static LogRotationResult NothingToDo(string name) => new(name, true, "file does not exist");
}

/// <summary>
/// Просмотр логов каталога logging.directory и внешняя ротация (сдвиг файлов). Чтение —
/// всегда: писатели держат файлы с FileShare.Read. Ротация извне работает только для
/// файлов, не открытых другим процессом: файл, который держит GUI или живая команда,
/// ротируется самим владельцем (FileLog.RotateNow), занятые здесь честно отмечаются
/// как locked.
/// </summary>
public sealed class LogStore(string directory)
{
    public string Directory { get; } = directory;

    /// <summary>Все лог-файлы каталога (текущие и ротированные .1/.2/...), по имени.</summary>
    public IReadOnlyList<LogFileInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.GetFiles(Directory)
            .Select(path => new FileInfo(path))
            .Where(file => IsLogFile(file.Name))
            .Select(file => new LogFileInfo(file.Name, file.Length, file.LastWriteTime))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Последние строки лога; null — файла нет. Имя — точное имя файла или без «.log»
    /// (chain → chain.log). Читает с конца файла: лог может быть больше хвоста на порядки.
    /// </summary>
    public LogTail? ReadTail(string name, int lines)
    {
        var path = ResolvePath(name);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var tailBytes = Math.Min(stream.Length, 256 * 1024);
        stream.Seek(-tailBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        var all = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        if (all.Length > 0 && all[^1].Length == 0)
        {
            all = all[..^1]; // файл кончается переводом строки
        }

        var take = Math.Clamp(lines, 0, all.Length);
        return new LogTail(all.Skip(all.Length - take).ToList(), stream.Length);
    }

    /// <summary>
    /// Внешняя ротация одного лога: сдвиг age-цепочки (текущий → .1 → ... → drop за keep).
    /// Файл, открытый другим процессом, сдвинуть нельзя — результат это сообщает.
    /// </summary>
    public LogRotationResult Rotate(string name, int keep)
    {
        var path = ResolvePath(name);
        if (path is null)
        {
            return new LogRotationResult(name, false, "unknown log name");
        }

        if (!File.Exists(path))
        {
            return LogRotationResult.NothingToDo(name);
        }

        try
        {
            LogRotation.Shift(path, keep);
            return LogRotationResult.Done(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LogRotationResult(name, false, $"locked by another process ({ex.Message})");
        }
    }

    private string? ResolvePath(string name)
    {
        if (name.Contains("..") || Path.IsPathRooted(name)
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null; // только имена файлов внутри каталога логов
        }

        var direct = Path.Combine(Directory, name);
        if (File.Exists(direct))
        {
            return direct;
        }

        var withLog = Path.Combine(Directory, name + ".log");
        return File.Exists(withLog) ? withLog : direct;
    }

    /// <summary>Текущие логи (*.log) и их ротированные копии (*.log.1, *.log.12, ...).</summary>
    internal static bool IsLogFile(string name)
    {
        if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var index = name.LastIndexOf(".log.", StringComparison.OrdinalIgnoreCase);
        return index >= 0 && name.AsSpan(index + 5).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
