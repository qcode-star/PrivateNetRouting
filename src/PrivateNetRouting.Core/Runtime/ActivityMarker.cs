namespace PrivateNetRouting.Core.Runtime;

/// <summary>
/// Proxy activity marker: the file mtime = the time of the last proxied request.
/// Updated no more often than once per interval (the watchdog polls every 10 minutes — more often is not needed);
/// file system errors are silently swallowed: the marker is auxiliary.
/// </summary>
public sealed class ActivityMarker(string path, TimeSpan minInterval, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();

    // the throttle runs on the monotonic clock: a wall-clock step (NTP, resume from sleep) must
    // neither suppress touches nor let a burst through
    private long _lastTouchTimestamp;
    private bool _touched;

    public string Path { get; } = path;

    public void Touch()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_touched && _time.GetElapsedTime(_lastTouchTimestamp, now) < minInterval)
            {
                return;
            }

            _lastTouchTimestamp = now;
            _touched = true;
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
            // the marker is auxiliary: silently normal
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
