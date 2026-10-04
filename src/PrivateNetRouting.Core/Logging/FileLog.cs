namespace PrivateNetRouting.Core.Logging;

/// <summary>
/// Сдвиг age-файлов ротации: current → .1, .1 → .2, ..., за keep — удаляется.
/// Общая логика стартовой (по размеру) и ручной ротации; вызывающий решает,
/// критичны ли ошибки (стартовая — проглатывает, ручная — сообщает).
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

    /// <summary>Ротация на старте: сдвигаем только переросший порог файл; ошибки не критичны.</summary>
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
            // ротация не критична: пишем в старый файл дальше
        }
    }
}

/// <summary>
/// Простой потокобезопасный файловый лог с уровнями (logging.level конфига, по умолчанию
/// Info). Политика прежнего набора: в Info — разовые события (старт/стоп/варнинги/ошибки),
/// периодические и per-request подробности (таймеры, коннекты, резолвы) — Debug и в штатном
/// режиме молчат; ротация по размеру выполняется один раз на старте процесса, ручная —
/// по запросу diagnostics (файл в этот момент принадлежит только этому процессу).
/// Если файл уже держит другой процесс (GUI в трее + pnr из терминала), лог уходит
/// в персональный файл &lt;имя&gt;.&lt;pid&gt;.log — два писателя в один файл не допускаются.
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
    /// Ручная ротация живого лога: текущий файл уходит в .1, запись продолжается в новый.
    /// Работает только у владельца файла: файл, открытый другим процессом, сдвинуть нельзя
    /// (внешняя ротация — LogStore, она честно сообщает о занятых файлах).
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
                // сдвиг не удался (например, .1 держит кто-то ещё) — пишем в тот же файл дальше
            }

            _writer = OpenWriter(FilePath).Writer;
            _writer.Flush();
        }
    }

    /// <summary>
    /// Открывает лог на запись; если файл занят другим процессом — откат на персональный
    /// файл &lt;имя&gt;.&lt;pid&gt;.log (второй писатель в тот же файл ломал бы содержимое).
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
