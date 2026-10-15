namespace PrivateNetRouting.Core.Logging;

/// <summary>
/// Shifting the rotation age files: current → .1, .1 → .2, ..., beyond keep — deleted.
/// Shared logic of the startup, size-based and manual rotation; the caller decides
/// whether errors are critical (startup/size — swallow them, manual — reports).
/// </summary>
internal static class LogRotation
{
    public static void Shift(string path, int keep)
    {
        var full = Path.GetFullPath(path);

        // The current file goes first: a file held by another process fails here, before any
        // age file is touched, so a failed rotation never destroys the existing chain.
        string? staged = null;
        if (File.Exists(full))
        {
            staged = $"{full}.rotating";
            File.Move(full, staged, overwrite: true);
        }

        try
        {
            for (var i = keep - 1; i > 0; i--)
            {
                var older = $"{full}.{i}";
                if (File.Exists(older))
                {
                    File.Move(older, $"{full}.{i + 1}", overwrite: true);
                }
            }

            if (staged is not null)
            {
                File.Move(staged, $"{full}.1", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (staged is not null && File.Exists(staged) && !File.Exists(full))
            {
                File.Move(staged, full);
            }

            throw;
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
            // rotation is not critical: the first record will retry by size
        }
    }
}

/// <summary>
/// Simple thread-safe file log with levels (logging.level of the config, default
/// Info). Logging policy: Info — one-off events (start/stop/warnings/errors),
/// periodic and per-request details (timers, connects, resolves) — Debug and silent in normal
/// operation. All writers (the GUI in the tray + pnr from a terminal, detached workers) write
/// to the same single file &lt;name&gt;.log: handles are opened with FileShare.ReadWrite and the
/// stream seeks to the end before every line, so the records do not push each other out.
/// Size rotation happens not only at start: before a record into an overgrown file (maxBytes)
/// the age chain shifts (current → .1 → ... beyond keep — deleted); if the chain is held by
/// another process, the rotation is postponed until RotateNow or the next process start.
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly object _gate = new();
    private readonly int _maxBytes;
    private readonly int _keep;
    private StreamWriter _writer;
    private bool _sizeRotationPostponed;

    public string FilePath { get; }

    public LogLevel Level { get; }

    public FileLog(string path, LogLevel level = LogLevel.Info, int maxBytes = 1_000_000, int keep = 2)
    {
        Level = level;
        _maxBytes = maxBytes;
        _keep = keep;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LogRotation.RotateAtStartup(path, maxBytes, keep);
        _writer = CreateWriter(path);
        FilePath = path;
    }

    public bool IsDebugEnabled => Level <= LogLevel.Debug;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>
    /// Manual rotation of the live log: the current file goes to .1, writing continues in a new one.
    /// It works while no other process holds the file or its age chain (external rotation —
    /// LogStore, it honestly reports locked files).
    /// </summary>
    public void RotateNow()
    {
        lock (_gate)
        {
            _writer.Dispose();
            ShiftChain();
            _writer = CreateWriter(FilePath);
            _writer.Flush();
        }
    }

    /// <summary>
    /// Opens the log for writing. The file is shared: another writer (the GUI + pnr) opens the
    /// same file rather than a copy, and readers (LogStore tail, rotation checks) are admitted too.
    /// </summary>
    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        stream.Seek(0, SeekOrigin.End);
        return new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void Write(LogLevel level, string message)
    {
        if (level < Level)
        {
            return;
        }

        lock (_gate)
        {
            RotateBySize();
            // re-seek for a fellow writer that appended while we were idle
            _writer.BaseStream.Seek(0, SeekOrigin.End);
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {LevelTag(level)} {message}");
            _writer.Flush();
        }
    }

    /// <summary>
    /// Size rotation during operation: before the next record an overgrown file shifts the age
    /// chain. A chain held by another process cannot be shifted — the rotation is postponed
    /// (one attempt per process, not one per record) until RotateNow or the next start.
    /// </summary>
    private void RotateBySize()
    {
        if (_sizeRotationPostponed || _writer.BaseStream.Length < _maxBytes)
        {
            return;
        }

        _writer.Dispose();
        ShiftChain();
        _writer = CreateWriter(FilePath);
        _writer.Flush();
    }

    private void ShiftChain()
    {
        try
        {
            LogRotation.Shift(FilePath, _keep);
            _sizeRotationPostponed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the file or its age chain is held by another process — keep appending to the same file
            _sizeRotationPostponed = true;
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
