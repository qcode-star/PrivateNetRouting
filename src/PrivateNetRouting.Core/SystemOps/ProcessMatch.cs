namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// Identification of "our" processes by a statedir / pipe path in the command line. A plain
/// substring is not enough: a statedir that is a prefix of another one (`…\sidecar` and
/// `…\sidecar-old`) would claim a foreign daemon — the value has to stand alone as an argument
/// value, and a process found by a scan is re-checked right before it is killed (a PID is reused).
/// </summary>
public static class ProcessMatch
{
    /// <summary>
    /// True when <paramref name="value"/> occurs in the command line as a whole argument value:
    /// preceded by the start, whitespace, a quote or '=' and followed by the end, whitespace or a
    /// quote (a path separator after it would make it a parent of some other directory).
    /// </summary>
    public static bool ContainsArgumentValue(string commandLine, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        var from = 0;
        while (from <= commandLine.Length - value.Length)
        {
            var index = commandLine.IndexOf(value, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var end = index + value.Length;
            var startsClean = index == 0 || IsBoundaryBefore(commandLine[index - 1]);
            var endsClean = end == commandLine.Length || IsBoundaryAfter(commandLine[end]);
            if (startsClean && endsClean)
            {
                return true;
            }

            from = index + 1;
        }

        return false;
    }

    private static bool IsBoundaryBefore(char c) => char.IsWhiteSpace(c) || c is '"' or '\'' or '=';

    private static bool IsBoundaryAfter(char c) => char.IsWhiteSpace(c) || c is '"' or '\'';

    /// <summary>Processes of the given image name whose command line carries <paramref name="value"/> as an argument value.</summary>
    public static IReadOnlyList<FoundProcess> FindByArgument(this IProcessFinder finder, string processName, string value) =>
        finder.Find(processName, value)
            .Where(p => ContainsArgumentValue(p.CommandLine, value))
            .ToList();

    /// <summary>
    /// The process with this PID still carries the value: the check right before a kill, since the
    /// PID may have been reused between the scan and now. A process that is gone needs no kill.
    /// </summary>
    public static bool StillCarriesArgument(this IProcessFinder finder, FoundProcess found, string value) =>
        finder.FindById(found.Id) is { } current && ContainsArgumentValue(current.CommandLine, value);
}
