namespace PrivateNetRouting.Core.Runtime;

/// <summary>
/// Маркер активности прокси: mtime файла = время последнего проксированного запроса.
/// Обновляется не чаще раза в интервал (сторож опрашивает раз в 10 минут — чаще не нужно);
/// ошибки файловой системы молча проглатываются: маркер вспомогательный.
/// </summary>
public sealed class ActivityMarker(string path, TimeSpan minInterval, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _lastTouch = DateTimeOffset.MinValue;

    public string Path { get; } = path;

    public void Touch()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _lastTouch < minInterval)
            {
                return;
            }

            _lastTouch = now;
        }

        try
        {
            if (File.Exists(Path))
            {
                File.SetLastWriteTimeUtc(Path, _time.GetUtcNow().UtcDateTime);
            }
            else
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, string.Empty);
                File.SetLastWriteTimeUtc(Path, _time.GetUtcNow().UtcDateTime);
            }
        }
        catch (IOException)
        {
            // маркер вспомогательный: тихая норма
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
