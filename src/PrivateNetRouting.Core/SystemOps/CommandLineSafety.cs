using System.Text.RegularExpressions;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// Values that end up on a cmd.exe command line (<see cref="ProcessOps.StartViaCmd"/>): cmd
/// interprets quotes and the metacharacters &amp; | &lt; &gt; ^ % itself, so a value from the
/// user-writable config is refused unless it is provably inert. <see cref="Configuration.AppConfigValidator"/>
/// reports the same shapes up front by config key; the process launchers re-check the expanded
/// value right before the spawn, because the elevated helper reads its own copy of the file.
/// </summary>
public static partial class CommandLineSafety
{
    /// <summary>The only socket shape handed to cmd: a local named pipe (never a file or UNC path).</summary>
    [GeneratedRegex(@"^\\\\\.\\pipe\\[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PipePath();

    public static bool IsValidPipePath(string value) => PipePath().IsMatch(value);

    /// <summary>True when the value cannot change the structure of a cmd.exe line.</summary>
    public static bool IsInert(string value) =>
        !string.IsNullOrEmpty(value)
        && value.All(c => c >= ' ' && c is not ('"' or '&' or '|' or '<' or '>' or '^' or '%'));

    /// <summary>The value, or an <see cref="InvalidDataException"/> naming the config key it came from.</summary>
    public static string RequireInert(string key, string value) =>
        IsInert(value)
            ? value
            : throw new InvalidDataException(
                $"{key}: '{value}' contains a cmd.exe metacharacter — refusing to build a command line with it");

    /// <summary>The pipe path, or an <see cref="InvalidDataException"/> naming the config key it came from.</summary>
    public static string RequirePipePath(string key, string value) =>
        IsValidPipePath(value)
            ? value
            : throw new InvalidDataException(
                $"{key}: '{value}' is not a local named-pipe path (expected \\\\.\\pipe\\<name>)");
}
