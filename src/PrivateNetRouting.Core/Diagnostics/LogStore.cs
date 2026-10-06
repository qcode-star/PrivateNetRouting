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
/// Viewing the logs of the logging.directory and external rotation (shifting files). Reading is
/// always allowed: writers hold files with FileShare.Read. External rotation works only for
/// files not opened by another process: a file held by the GUI or a live command is
/// rotated by its owner (FileLog.RotateNow), busy ones are honestly reported here
/// as locked.
/// </summary>
public sealed class LogStore(string directory)
{
    public string Directory { get; } = directory;

    /// <summary>All log files of the directory (current and rotated .1/.2/...), by name.</summary>
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
    /// Last lines of the log; null — the file does not exist. The name is the exact file name or
    /// without ".log" (chain -> chain.log). Reads from the end of the file: the log can be orders
    /// of magnitude larger than the tail.
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
            all = all[..^1]; // the file ends with a newline
        }

        var take = Math.Clamp(lines, 0, all.Length);
        return new LogTail(all.Skip(all.Length - take).ToList(), stream.Length);
    }

    /// <summary>
    /// External rotation of one log: shifting the age chain (current -> .1 -> ... -> drop past keep).
    /// A file opened by another process cannot be shifted — the result says so.
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
            return null; // only file names inside the log directory
        }

        var direct = Path.Combine(Directory, name);
        if (File.Exists(direct))
        {
            return direct;
        }

        var withLog = Path.Combine(Directory, name + ".log");
        return File.Exists(withLog) ? withLog : direct;
    }

    /// <summary>Current logs (*.log) and their rotated copies (*.log.1, *.log.12, ...).</summary>
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
