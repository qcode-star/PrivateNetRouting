namespace PrivateNetRouting.Core.Logging;

/// <summary>
/// Простой потокобезопасный файловый лог. Политика прежнего набора: старт/стоп/варнинги/ошибки,
/// ничего per-request в штатном режиме; ротация по размеру выполняется один раз на старте
/// процесса, во время работы файл не трогается.
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public string FilePath { get; }

    public FileLog(string path, int maxBytes = 1_000_000, int keep = 2)
    {
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateAtStartup(path, maxBytes, keep);
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}");
            _writer.Flush();
        }
    }

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
