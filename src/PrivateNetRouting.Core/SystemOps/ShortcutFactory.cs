using System.Runtime.InteropServices;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// Creating a Windows shortcut (.lnk). Behind an interface: tests use a fake, and the
/// implementation is the only place that touches COM.
/// </summary>
public interface IShortcutFactory
{
    /// <param name="shortcutPath">Full path of the .lnk to create or overwrite.</param>
    /// <param name="targetPath">The executable the shortcut starts.</param>
    /// <param name="arguments">Command-line arguments, already quoted for the Windows command line.</param>
    /// <param name="workingDirectory">Working directory of the started process.</param>
    /// <param name="description">Tooltip text of the shortcut.</param>
    void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string description);
}

/// <summary>
/// Reading and re-iconing shortcuts that already exist (the profile's desktop identity). Behind an
/// interface for the same reason: the tests never touch COM.
/// </summary>
public interface IShortcutStore
{
    /// <summary>What an existing .lnk starts and how it looks; null when there is no such file.</summary>
    ShortcutProperties? ReadShortcut(string shortcutPath);

    /// <summary>Replaces the icon of an existing .lnk; its target and arguments stay as they are.</summary>
    void SetShortcutIcon(string shortcutPath, string iconLocation);
}

/// <summary>A shortcut as the icon refresher sees it: <c>IconLocation</c> is <c>path,index</c> or empty.</summary>
public sealed record ShortcutProperties(string TargetPath, string Arguments, string IconLocation);

/// <summary>
/// Shortcut work through the WScript.Shell COM object — the same path Explorer uses, no
/// privileges required (the desktop is writable by the owner), no PowerShell process.
/// </summary>
public sealed class WScriptShortcutFactory : IShortcutFactory, IShortcutStore
{
    public void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string description) =>
        WithShortcut<object?>(shortcutPath, save: true, link =>
        {
            link.TargetPath = targetPath;
            link.Arguments = arguments;
            link.WorkingDirectory = workingDirectory;
            link.Description = description;
            link.IconLocation = targetPath + ",0";
            return null;
        });

    public ShortcutProperties? ReadShortcut(string shortcutPath) =>
        File.Exists(shortcutPath)
            ? WithShortcut(shortcutPath, save: false, link => new ShortcutProperties(
                (string?)link.TargetPath ?? "",
                (string?)link.Arguments ?? "",
                (string?)link.IconLocation ?? ""))
            : null;

    public void SetShortcutIcon(string shortcutPath, string iconLocation) =>
        WithShortcut<object?>(shortcutPath, save: true, link =>
        {
            link.IconLocation = iconLocation;
            return null;
        });

    /// <summary>Opens the shortcut through WScript.Shell, runs <paramref name="work"/> and saves on request.</summary>
    private static T WithShortcut<T>(string shortcutPath, bool save, Func<dynamic, T> work)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("WScript.Shell is unavailable: cannot work with a shortcut");
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type)
                ?? throw new PlatformNotSupportedException("WScript.Shell did not start: cannot work with a shortcut");
            var link = ((dynamic)shell).CreateShortcut(shortcutPath);
            try
            {
                var result = work(link);
                if (save)
                {
                    link.Save();
                }

                return result;
            }
            finally
            {
                if (link is not null)
                {
                    Marshal.ReleaseComObject(link);
                }
            }
        }
        finally
        {
            if (shell is not null)
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }
}
