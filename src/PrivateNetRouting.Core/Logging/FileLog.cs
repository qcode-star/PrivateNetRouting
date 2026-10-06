namespace PrivateNetRouting.Core.Logging;

/// <summary>
/// Shifting the rotation age files: current → .1, .1 → .2, ..., beyond keep — deleted.
/// Shared logic of the startup (by size) and manual rotation; the caller decides
/// whether errors are critical (startup — swallows them, manual — reports).
/// </summary>
internal static class LogRotation
{
    public static void Shift(string path, int keep)
    {
        var full = Path.GetFullPath(path);
        for (var i = keep - 1; i > 0; i--)
        {
            var older = $"{full}.{i}";
            if (File.Exists(older))
            {
                File.Move(older, $"{full}.{i + 1}", overwrite: true);
            }
        }

        if (File.Exists(full))
        {
            File.Move(full, $"{full}.1", overwrite: true);
        }
    }

    /// <summary>Rotation at start: shift only a file that outgrew the threshold; errors are not critical.</summary>
    public static void RotateAtStartup(string path, int maxBytes, int keep)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) || new FileInfo(full).Length < maxBytes)
            {
                return;
            }

            Shift(full, keep);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // rotation is not critical: keep writing to the old file
        }
    }
}

/// <summary>
/// Simple thread-safe file log with levels (logging.level of the config, default
/// Info). Logging policy: Info — one-off events (start/stop/warnings/errors),
/// periodic and per-request details (timers, connects, resolves) — Debug and silent in normal
/// operation; size-based rotation is performed once at process start, manual —
/// on a diagnostics request (at that moment the file belongs only to this process).
/// If the file is already held by another process (GUI in the tray + pnr from a terminal), the log goes
/// to a personal file &lt;name&gt;.&lt;pid&gt;.log — two writers to one file are not allowed.
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly object _gate = new();
    private readonly int _keep;
    private StreamWriter _writer;

    public string FilePath { get; }

    public LogLevel Level { get; }

    public FileLog(string path, LogLevel level = LogLevel.Info, int maxBytes = 1_000_000, int keep = 2)
    {
        Level = level;
        _keep = keep;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LogRotation.RotateAtStartup(path, maxBytes, keep);
        (_writer, FilePath) = OpenWriter(path);
    }

    public bool IsDebugEnabled => Level <= LogLevel.Debug;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>
    /// Manual rotation of the live log: the current file goes to .1, writing continues in a new one.
    /// It works only for the file owner: a file opened by another process cannot be shifted
    /// (external rotation — LogStore, it honestly reports locked files).
    /// </summary>
    public void RotateNow()
    {
        lock (_gate)
        {
            _writer.Dispose();
            try
            {
                LogRotation.Shift(FilePath, _keep);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the shift failed (e.g. .1 is held by someone else) — keep writing to the same file
            }

            _writer = OpenWriter(FilePath).Writer;
            _writer.Flush();
        }
    }

    /// <summary>
    /// Opens the log for writing; if the file is held by another process — fallback to a personal
    /// file &lt;name&gt;.&lt;pid&gt;.log (a second writer to the same file would corrupt the content).
    /// </summary>
    private static (StreamWriter Writer, string Path) OpenWriter(string path)
    {
        try
        {
            return (CreateWriter(path), path);
        }
        catch (IOException)
        {
            var alternative = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path))!,
                $"{Path.GetFileNameWithoutExtension(path)}.{Environment.ProcessId}{Path.GetExtension(path)}");
            var writer = CreateWriter(alternative);
            writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} INFO log: {Path.GetFileName(path)} is held by another process, writing to {Path.GetFileName(alternative)}");
            writer.Flush();
            return (writer, alternative);
        }
    }

    private static StreamWriter CreateWriter(string path) =>
        new(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private void Write(LogLevel level, string message)
    {
        if (level < Level)
        {
            return;
        }

        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {LevelTag(level)} {message}");
            _writer.Flush();
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        _ => "ERROR",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
