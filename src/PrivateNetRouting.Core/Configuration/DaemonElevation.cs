namespace PrivateNetRouting.Core.Configuration;

/// <summary>How the sidecar daemon start/stop is elevated.</summary>
public enum DaemonElevationMode
{
    Auto = 0,
    Never = 1,
    Always = 2,
}

public static class DaemonElevationParser
{
    /// <summary>Config string ("auto"/"never"/"always") to a mode; unknown — Auto.</summary>
    public static DaemonElevationMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "never" => DaemonElevationMode.Never,
        "always" => DaemonElevationMode.Always,
        _ => DaemonElevationMode.Auto,
    };
}
