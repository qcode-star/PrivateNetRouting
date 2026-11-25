using System.Text.RegularExpressions;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// Named profiles: one config file and one process tree per profile. The default profile is the
/// long-standing <c>%APPDATA%\PrivateNetRouting\appsettings.json</c>; <c>--profile &lt;name&gt;</c>
/// selects <c>%APPDATA%\PrivateNetRouting\profiles\&lt;name&gt;.json</c> instead. The name is the
/// identity every OS-level name is derived from (the GUI mutex and its events, the detached-worker
/// command lines, the window and tray title); the config's <c>profile.name</c> is display only.
/// </summary>
public static partial class AppProfile
{
    /// <summary>The global command-line option; it may stand anywhere among the arguments.</summary>
    public const string Option = "--profile";

    /// <summary>The identity of the config that needs no option; reserved, a profile cannot be named so.</summary>
    public const string DefaultName = "default";

    /// <summary>
    /// A profile name is a file name under the profiles directory: letters, digits, dot, dash and
    /// underscore, starting with a letter or a digit, at most 32 characters. No separators, no
    /// traversal, no trailing dots or spaces (Windows would silently trim them).
    /// </summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    /// <summary>
    /// The profile named by the arguments, or null for the default one. One option only; a missing
    /// value or an unusable name is an error (the caller reports it to its user).
    /// </summary>
    public static string? ParseName(IReadOnlyList<string> args)
    {
        string? found = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], Option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"{Option} needs a profile name");
            }

            var value = args[i + 1].Trim();
            if (!IsValidName(value))
            {
                throw new ArgumentException(
                    $"'{value}' is not a valid profile name: use letters, digits, '.', '-' or '_' (at most 32 characters, not '{DefaultName}')");
            }

            if (found is not null && !string.Equals(found, value, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{Option} is given more than once");
            }

            found = value;
            i++;
        }

        return found;
    }

    /// <summary>The arguments without their <see cref="Option"/> pairs — what the command dispatchers parse.</summary>
    public static IReadOnlyList<string> WithoutOption(IReadOnlyList<string> args)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], Option, StringComparison.OrdinalIgnoreCase))
            {
                i++; // the value (ParseName has already validated the pair)
                continue;
            }

            result.Add(args[i]);
        }

        return result;
    }

    /// <summary>
    /// The profile a split command line names, or null for the default one. Options are looked up
    /// among the arguments only (the first element is the program).
    /// </summary>
    public static string? NameFromArguments(IReadOnlyList<string> arguments)
    {
        for (var i = 1; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], Option, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    /// <summary>The profile's identity, config path and the name as the user typed it.</summary>
    public static ProfileContext Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProfileContext.Default;
        }

        var typed = name.Trim();
        var canonical = typed.ToLowerInvariant();
        if (!IsValidName(canonical))
        {
            throw new ArgumentException(
                $"'{typed}' is not a valid profile name: use letters, digits, '.', '-' or '_' (at most 32 characters, not '{DefaultName}')");
        }

        var directory = Path.GetDirectoryName(AppConfigStore.GetDefaultConfigPath())!;
        return new ProfileContext(canonical, Path.Combine(directory, "profiles", canonical + ".json"), typed);
    }

    /// <summary>
    /// A named profile must exist: a config created from defaults would silently use the default
    /// ports and log directory and collide with the running profile. Creation is explicit
    /// (<c>pnr --profile &lt;name&gt; config init</c>); the default profile still appears on first launch.
    /// </summary>
    public static void EnsureConfigured(ProfileContext profile)
    {
        if (!profile.IsDefault && !File.Exists(profile.ConfigPath))
        {
            throw new FileNotFoundException(
                $"profile '{profile.Name}' is not configured ({profile.ConfigPath}); create it with \"pnr {Option} {profile.Name} config init\"",
                profile.ConfigPath);
        }
    }

    private static bool IsValidName(string name)
    {
        var normalized = name.ToLowerInvariant();
        return normalized.Length <= 32
            && !string.Equals(normalized, DefaultName, StringComparison.Ordinal)
            && !normalized.EndsWith('.')
            && ValidName().IsMatch(normalized);
    }
}

/// <summary>
/// One profile of the application: its identity (the canonical, lower-case name), the config file
/// it runs from and the name the user typed (the display fallback until <c>profile.name</c> says
/// otherwise). Every OS-level name a process creates is scoped by <see cref="Name"/>.
/// </summary>
public sealed record ProfileContext(string Name, string ConfigPath, string? Label = null)
{
    /// <summary>No <c>--profile</c> was given: the long-standing single config.</summary>
    public static ProfileContext Default { get; } = new(AppProfile.DefaultName, AppConfigStore.GetDefaultConfigPath());

    public bool IsDefault => string.Equals(Name, AppProfile.DefaultName, StringComparison.Ordinal);

    /// <summary>The suffix of the profile's OS-level names (mutex, events); null — the default names.</summary>
    public string? NameSuffix => IsDefault ? null : Name;

    /// <summary>
    /// What the window title and the tray show after the product name: <c>profile.name</c> when it
    /// is set, otherwise the typed argument; null — the default profile with no configured name.
    /// </summary>
    public string? DisplayName(AppConfig config)
    {
        var configured = config.Profile.Name.Trim();
        if (configured.Length > 0)
        {
            return configured;
        }

        return IsDefault ? null : Label ?? Name;
    }
}
