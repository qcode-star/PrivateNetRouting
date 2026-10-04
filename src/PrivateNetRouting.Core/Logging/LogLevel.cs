namespace PrivateNetRouting.Core.Logging;

/// <summary>Уровень записи: порядок значений — убывающая подробность.</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public static class LogLevelParser
{
    /// <summary>Строка конфига ("debug"/"info"/"warn"/"error") в уровень; незнакомое — Info.</summary>
    public static LogLevel Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}
