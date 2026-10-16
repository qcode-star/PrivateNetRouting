using PrivateNetRouting.Core.Chain;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// The command line of the elevated helper (a runas copy of the application): the command, the
/// result file the parent waits for and the config the parent runs with. The config path matters
/// under over-the-shoulder UAC, where the administrator's own %APPDATA% would otherwise be
/// created and used instead of the config of the user who asked. For the same reason the asking
/// side passes its own values of the profile variables the config paths are written with
/// (`--env NAME=value`): the helper expands %APPDATA%, %LOCALAPPDATA% and %USERPROFILE% of the
/// statedir, the log directory and the other paths with them, not with the administrator's.
/// </summary>
public static class ElevatedHelperArguments
{
    public const string ResultFileOption = "--result-file";
    public const string ConfigOption = "--config";
    public const string EnvOption = "--env";

    /// <summary>The profile variables handed to the helper — the ones the config paths use.</summary>
    public static IReadOnlyList<string> ProfileVariables { get; } = ["APPDATA", "LOCALAPPDATA", "USERPROFILE"];

    public static string Build(
        string command,
        string resultFile,
        string? configPath,
        IReadOnlyDictionary<string, string>? profileEnvironment = null)
    {
        var arguments = $"{command} {ResultFileOption} {ProcessOps.Quote(resultFile)}";
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            arguments = $"{arguments} {ConfigOption} {ProcessOps.Quote(configPath)}";
        }

        foreach (var (name, value) in profileEnvironment ?? new Dictionary<string, string>())
        {
            if (IsValidProfileEntry(name, value))
            {
                arguments = $"{arguments} {EnvOption} {ProcessOps.Quote($"{name}={value}")}";
            }
        }

        return arguments;
    }

    /// <summary>This process's values of the profile variables (the asking side); invalid or missing ones are left out.</summary>
    public static IReadOnlyDictionary<string, string> CurrentProfileEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ProfileVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null && IsValidProfileEntry(name, value))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>Every value following <paramref name="name"/> (a repeatable option).</summary>
    public static IReadOnlyList<string> ParseOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                values.Add(args[i + 1]);
                i++;
            }
        }

        return values;
    }

    /// <summary>
    /// The helper side: the `--env` entries as a variable map. Only the known profile variables
    /// with an absolute path are accepted — anything else is not what the application passes,
    /// and the helper refuses to run (InvalidOperationException) instead of guessing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToProfileEnvironment(IReadOnlyList<string>? entries)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries ?? [])
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            var name = separator > 0 ? entry[..separator] : "";
            var value = separator > 0 ? entry[(separator + 1)..] : "";
            if (!IsValidProfileEntry(name, value))
            {
                throw new InvalidOperationException($"the elevated helper got an unexpected environment entry: {entry}");
            }

            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// A profile variable of ours with an absolute path that survives the quoting of the helper
    /// command line: no quotes, no %, no line breaks, no trailing backslash (it would escape the
    /// closing quote).
    /// </summary>
    internal static bool IsValidProfileEntry(string name, string value) =>
        ProfileVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(value)
        && Path.IsPathFullyQualified(value)
        && value.IndexOfAny(['"', '%', '\r', '\n']) < 0
        && !value.EndsWith('\\');

    /// <summary>The value following an option; null when the option is absent or has no value.</summary>
    public static string? ParseOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Whether this argument list is a call of the elevated helper (chain or guard command).</summary>
    public static bool IsHelperCommand(string? command) =>
        command is ElevatedChainCommands.StartDaemon or ElevatedChainCommands.StopChain
        || (command is not null && Guard.GuardElevatedCommands.IsCommand(command));
}
