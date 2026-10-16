using System.Security.AccessControl;

namespace PrivateNetRouting.Core.Logging;

/// <summary>
/// Shifting the rotation age files: current → .1, .1 → .2, ..., beyond keep — deleted.
/// Shared logic of the startup, size-based and manual rotation; the caller decides
/// whether errors are critical (startup/size — swallow them, manual — reports).
/// </summary>
internal static class LogRotation
{
    /// <summary>
    /// The staging name of the current file during a shift: &lt;log&gt;.rotating-&lt;pid&gt;-&lt;random&gt;.
    /// Unique per rotation, so concurrent rotations of other processes never share (and overwrite)
    /// one staging file; the PID tells a live rotation from the leftover of a crashed one.
    /// </summary>
    private const string StagingSuffix = ".rotating";

    /// <summary>Serializes the shifts of this process (all FileLog instances and LogStore).</summary>
    private static readonly object Gate = new();

    public static void Shift(string path, int keep)
    {
        var full = Path.GetFullPath(path);
        lock (Gate)
        {
            ShiftLocked(full, keep);
        }
    }

    private static void ShiftLocked(string full, int keep)
    {
        // The current file goes first: a file held by another process fails here, before any
        // age file is touched, so a failed rotation never destroys the existing chain.
        string? staged = null;
        if (File.Exists(full))
        {
            staged = NewStagingName(full);
            File.Move(full, staged);
        }

        try
        {
            FoldOrphans(full, staged);
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
            // Put the staged records back without loss: as the current log when nobody re-created
            // it meanwhile, otherwise the staging file stays where it is and the next rotation
            // folds it into the age chain (FoldOrphans).
            if (staged is not null && File.Exists(staged) && !File.Exists(full))
            {
                try
                {
                    File.Move(staged, full);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    // a writer re-created the log in between: the staging file is kept for the next rotation
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Staging files left behind by a crashed rotation (or by a failed one of this process) hold
    /// the records of a former current log: older than the current one, newer than every age
    /// file. They are appended to the end of .1 (or become .1), oldest first, before the chain
    /// shifts — so they land just behind the file being rotated now. A staging file of another
    /// live process is its rotation in flight and is left alone. Each orphan is claimed by
    /// renaming it to a staging name of this process first, so two rotations never fold the same
    /// one; an orphan that cannot be folded right now keeps its claimed name for the next try.
    /// </summary>
    private static void FoldOrphans(string full, string? inFlight)
    {
        foreach (var orphan in FindOrphans(full))
        {
            if (string.Equals(orphan, inFlight, StringComparison.OrdinalIgnoreCase))
            {
                continue; // the current log this very shift has just staged
            }

            var claimed = NewStagingName(full);
            try
            {
                File.Move(orphan, claimed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // claimed by another rotation meanwhile, or held
            }

            var first = $"{full}.1";
            if (!File.Exists(first))
            {
                File.Move(claimed, first);
                continue;
            }

            using (var target = new FileStream(first, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var source = new FileStream(claimed, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                source.CopyTo(target);
            }

            File.Delete(claimed);
        }
    }

    /// <summary>The orphaned staging files of a log, oldest first.</summary>
    internal static IReadOnlyList<string> FindOrphans(string full)
    {
        var directory = Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        var prefix = Path.GetFileName(full) + StagingSuffix;
        return Directory.GetFiles(directory, prefix + "*")
            .Where(file => IsOrphan(Path.GetFileName(file)[prefix.Length..]))
            .OrderBy(File.GetLastWriteTimeUtc)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The tail after "&lt;log&gt;.rotating": empty (the fixed staging name of older builds) or
    /// "-&lt;pid&gt;-&lt;random&gt;". A staging file of this process is an orphan too — the shifts of
    /// this process are serialized, so none of its own is in flight.
    /// </summary>
    private static bool IsOrphan(string tail)
    {
        if (tail.Length == 0)
        {
            return true;
        }

        var parts = tail.Split('-');
        if (parts.Length != 3 || parts[0].Length != 0 || !int.TryParse(parts[1], out var pid))
        {
            return false; // not a staging name of ours
        }

        return pid == Environment.ProcessId || !IsProcessAlive(pid);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // cannot tell: leave its file alone
        }
    }

    private static string NewStagingName(string full) =>
        $"{full}{StagingSuffix}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <summary>
    /// Rotation at start: shift a file that outgrew the threshold, or fold in the staging files
    /// of a crashed rotation; errors are not critical.
    /// </summary>
    public static void RotateAtStartup(string path, int maxBytes, int keep)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) && new FileInfo(full).Length >= maxBytes)
            {
                Shift(full, keep);
            }
            else if (FindOrphans(full).Count > 0)
            {
                lock (Gate)
                {
                    FoldOrphans(full, inFlight: null);
                }
            }
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
/// to the same single file &lt;name&gt;.log. No handle is kept between records: every record opens
/// the file for appending (FileShare.ReadWrite | Delete) and closes it, so a long-lived process
/// never pins the file against another process's rotation, a rename never leaves a writer
/// appending to the renamed copy, and the records of concurrent writers do not push each other out.
/// Size rotation happens not only at start: before a record into an overgrown file (maxBytes)
/// the age chain shifts (current → .1 → ... beyond keep — deleted); a chain that cannot be
/// shifted right now (held by a foreign process) is retried on a later record, not given up on.
/// </summary>
public sealed class FileLog : IDisposable
{
    /// <summary>How long a failed size rotation waits before the next record tries it again.</summary>
    private static readonly TimeSpan DefaultRotationRetryInterval = TimeSpan.FromSeconds(30);

    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly int _maxBytes;
    private readonly int _keep;
    private readonly TimeSpan _rotationRetryInterval;
    private DateTime _nextRotationAttempt = DateTime.MinValue;

    public string FilePath { get; }

    public LogLevel Level { get; }

    public FileLog(
        string path,
        LogLevel level = LogLevel.Info,
        int maxBytes = 1_000_000,
        int keep = 2,
        TimeSpan? rotationRetryInterval = null)
    {
        Level = level;
        _maxBytes = maxBytes;
        _keep = keep;
        _rotationRetryInterval = rotationRetryInterval ?? DefaultRotationRetryInterval;
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LogRotation.RotateAtStartup(path, maxBytes, keep);
        using var created = OpenForAppend(path); // the file exists from the start
    }

    public bool IsDebugEnabled => Level <= LogLevel.Debug;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>
    /// Manual rotation of the live log: the current file goes to .1, writing continues in a new
    /// one. Returns false when the file or its age chain is held by another process (external
    /// rotation — LogStore — reports such files as locked).
    /// </summary>
    public bool RotateNow()
    {
        lock (_gate)
        {
            return ShiftChain();
        }
    }

    /// <summary>
    /// Opens the log for appending. The file is shared: another writer (the GUI + pnr) opens the
    /// same file rather than a copy, readers (LogStore tail) are admitted, and Delete sharing lets
    /// another process rename it for rotation while it is open.
    /// </summary>
    private static FileStream OpenForAppend(string path) =>
        // AppendData without WriteData: the kernel positions every write at the current end of file
        // atomically, so records of concurrent processes never overwrite or interleave each other
        // (FileMode.Append only seeks to the end once at open, and another writer may move it after)
        new FileInfo(path).Create(
            FileMode.OpenOrCreate,
            FileSystemRights.AppendData,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.None,
            fileSecurity: null);

    private void Write(LogLevel level, string message)
    {
        if (level < Level)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {LevelTag(level)} {message}{Environment.NewLine}";
        lock (_gate)
        {
            RotateBySize();
            using var stream = OpenForAppend(FilePath);
            stream.Write(Utf8.GetBytes(line));
        }
    }

    /// <summary>
    /// Size rotation during operation: before the next record an overgrown file shifts the age
    /// chain. When that fails (a foreign process holds the file), the next attempt waits
    /// the retry interval instead of repeating on every record.
    /// </summary>
    private void RotateBySize()
    {
        if (DateTime.UtcNow < _nextRotationAttempt)
        {
            return;
        }

        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        ShiftChain();
    }

    private bool ShiftChain()
    {
        try
        {
            LogRotation.Shift(FilePath, _keep);
            _nextRotationAttempt = DateTime.MinValue;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the file or its age chain is held by another process — keep appending to the same
            // file and try again later
            _nextRotationAttempt = DateTime.UtcNow + _rotationRetryInterval;
            return false;
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
        // nothing is held between records
    }
}
