namespace PrivateNetRouting.Core.Logging;

/// <summary>
/// Простой потокобезопасный файловый лог с уровнями (logging.level конфига, по умолчанию
/// Info). Политика прежнего набора: в Info — разовые события (старт/стоп/варнинги/ошибки),
/// периодические и per-request подробности (таймеры, коннекты, резолвы) — Debug и в штатном
/// режиме молчат; ротация по размеру выполняется один раз на старте процесса.
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public string FilePath { get; }

    public LogLevel Level { get; }

    public FileLog(string path, LogLevel level = LogLevel.Info, int maxBytes = 1_000_000, int keep = 2)
    {
        FilePath = path;
        Level = level;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateAtStartup(path, maxBytes, keep);
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public bool IsDebugEnabled => Level <= LogLevel.Debug;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message) => Write(LogLevel.Error, message);

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

    private static void RotateAtStartup(string path, int maxBytes, int keep)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) || new FileInfo(full).Length < maxBytes)
            {
                return;
            }

            for (var i = keep - 1; i > 0; i--)
            {
                var older = $"{full}.{i}";
                if (File.Exists(older))
                {
                    File.Move(older, $"{full}.{i + 1}", overwrite: true);
                }
            }

            File.Move(full, $"{full}.1", overwrite: true);
        }
        catch (IOException)
        {
            // ротация не критична: пишем в старый файл дальше
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
