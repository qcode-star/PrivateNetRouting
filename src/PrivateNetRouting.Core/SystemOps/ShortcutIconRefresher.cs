using System.Runtime.InteropServices;
using System.Text;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// Keeps the desktop shortcuts that start this profile wearing the profile's icon. The exe carries
/// the neutral "both apps" icon only, so a single-app profile points its own shortcuts at the brand
/// variant written to <see cref="AppIconFiles.Directory"/>. Only shortcuts whose target is this exe
/// and whose arguments name this profile are touched, and a shortcut whose icon the owner chose by
/// hand keeps it.
/// </summary>
public sealed class ShortcutIconRefresher
{
    private readonly IShortcutStore _shortcuts;
    private readonly FileLog _log;
    private readonly Func<string> _desktopDirectory;
    private readonly Func<string, bool> _fileExists;

    public ShortcutIconRefresher(
        IShortcutStore shortcuts,
        FileLog log,
        Func<string>? desktopDirectory = null,
        Func<string, bool>? fileExists = null)
    {
        _shortcuts = shortcuts;
        _log = log;
        _desktopDirectory = desktopDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>
    /// Points the profile's shortcuts at the variant's icon (back at the exe's own one for the
    /// neutral variant) and returns the shortcuts that changed, so the caller can report them.
    /// </summary>
    public IReadOnlyList<string> Apply(string exePath, ProfileContext profile, AppIconVariant variant)
    {
        var variantIcon = AppIconFiles.PathFor(variant);
        if (variantIcon is not null && !_fileExists(variantIcon))
        {
            // the host writes the file before it calls this: without it the shortcut would lose its
            // icon entirely, so nothing is touched
            _log.Warn($"icons: {variantIcon} is missing, the shortcuts keep their icon");
            return [];
        }

        var desired = variantIcon is null ? $"{exePath},0" : $"{variantIcon},0";
        var directory = _desktopDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var changed = new List<string>();
        foreach (var shortcutPath in Directory.EnumerateFiles(directory, "*.lnk"))
        {
            ShortcutProperties? shortcut;
            try
            {
                shortcut = _shortcuts.ReadShortcut(shortcutPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                _log.Warn($"icons: {Path.GetFileName(shortcutPath)} is unreadable: {ex.Message}");
                continue;
            }

            if (shortcut is null
                || !SamePath(shortcut.TargetPath, exePath)
                || !ServesProfile(shortcut.Arguments, profile))
            {
                continue;
            }

            if (!IsOurs(shortcut.IconLocation, exePath))
            {
                _log.Debug($"icons: {Path.GetFileName(shortcutPath)} keeps its own icon ({shortcut.IconLocation})");
                continue;
            }

            if (SameIconLocation(shortcut.IconLocation, desired, exePath))
            {
                continue;
            }

            try
            {
                _shortcuts.SetShortcutIcon(shortcutPath, desired);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                _log.Warn($"icons: {Path.GetFileName(shortcutPath)}: the icon was not set: {ex.Message}");
                continue;
            }

            changed.Add(shortcutPath);
            _log.Info($"icons: {Path.GetFileName(shortcutPath)} now shows the {Describe(variant)} icon");
        }

        return changed;
    }

    private static string Describe(AppIconVariant variant) => variant switch
    {
        AppIconVariant.Claude => "Claude",
        AppIconVariant.Codex => "Codex",
        _ => "neutral",
    };

    /// <summary>
    /// Whether the shortcut starts this profile: a named profile must be the one its arguments name,
    /// the default profile is the one without <c>--profile</c> (the same rule the host applies).
    /// </summary>
    private static bool ServesProfile(string arguments, ProfileContext profile)
    {
        var named = ProfileArgument(arguments);
        return profile.IsDefault
            ? named is null
            : string.Equals(named, profile.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>--profile</c> value of a command line, either <c>--profile name</c> or <c>--profile=name</c>.</summary>
    private static string? ProfileArgument(string arguments)
    {
        var tokens = SplitArguments(arguments);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith(AppProfile.Option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = token[AppProfile.Option.Length..];
            if (rest.StartsWith('='))
            {
                return rest[1..];
            }

            if (rest.Length == 0)
            {
                return i + 1 < tokens.Count ? tokens[i + 1] : null;
            }
        }

        return null;
    }

    /// <summary>Splits a shortcut's arguments on whitespace, keeping a quoted path in one token.</summary>
    private static List<string> SplitArguments(string arguments)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>
    /// Whether the shortcut's icon is the exe's own or one of ours: an icon the owner picked by hand
    /// is never replaced.
    /// </summary>
    private static bool IsOurs(string iconLocation, string exePath)
    {
        var (path, _) = SplitIconLocation(iconLocation);
        if (path.Length == 0)
        {
            return true; // no icon of its own: the shell shows the target exe's
        }

        if (SamePath(path, exePath))
        {
            return true;
        }

        var iconsDirectory = TryFullPath(AppIconFiles.Directory);
        var icon = TryFullPath(path);
        return iconsDirectory is not null
            && icon is not null
            && string.Equals(Path.GetDirectoryName(icon), iconsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the shortcut already shows the wanted icon; an empty location means the target's own
    /// icon, which is what the neutral variant asks for.
    /// </summary>
    private static bool SameIconLocation(string left, string right, string targetPath)
    {
        var (leftPath, leftIndex) = SplitIconLocation(left);
        var (rightPath, rightIndex) = SplitIconLocation(right);
        if (leftPath.Length == 0)
        {
            leftPath = targetPath;
        }

        if (rightPath.Length == 0)
        {
            rightPath = targetPath;
        }

        return leftIndex == rightIndex && SamePath(leftPath, rightPath);
    }

    /// <summary>Splits <c>path,index</c>; a missing index means the exe's first icon.</summary>
    private static (string Path, int Index) SplitIconLocation(string iconLocation)
    {
        var value = iconLocation.Trim();
        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out var index))
        {
            return (value[..comma].Trim().Trim('"'), index);
        }

        return (value.Trim('"'), 0);
    }

    private static bool SamePath(string left, string right) =>
        TryFullPath(left) is { } normalizedLeft
        && TryFullPath(right) is { } normalizedRight
        && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);

    private static string? TryFullPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
