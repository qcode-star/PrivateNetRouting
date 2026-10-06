namespace PrivateNetRouting.Core.Logging;

/// <summary>Record level: the order of values is decreasing verbosity.</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public static class LogLevelParser
{
    /// <summary>Config string ("debug"/"info"/"warn"/"error") to a level; unknown — Info.</summary>
    public static LogLevel Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}
