using PrivateNetRouting.Core.Chain;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// The command line of the elevated helper (a runas copy of the application): the command, the
/// result file the parent waits for and the config the parent runs with. The config path matters
/// under over-the-shoulder UAC, where the administrator's own %APPDATA% would otherwise be
/// created and used instead of the config of the user who asked.
/// </summary>
public static class ElevatedHelperArguments
{
    public const string ResultFileOption = "--result-file";
    public const string ConfigOption = "--config";

    public static string Build(string command, string resultFile, string? configPath)
    {
        var arguments = $"{command} {ResultFileOption} {ProcessOps.Quote(resultFile)}";
        return string.IsNullOrWhiteSpace(configPath)
            ? arguments
            : $"{arguments} {ConfigOption} {ProcessOps.Quote(configPath)}";
    }

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
