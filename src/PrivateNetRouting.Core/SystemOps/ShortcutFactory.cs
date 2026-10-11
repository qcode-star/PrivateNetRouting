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
/// Shortcut creation through the WScript.Shell COM object — the same path Explorer uses, no
/// privileges required (the desktop is writable by the owner), no PowerShell process.
/// </summary>
public sealed class WScriptShortcutFactory : IShortcutFactory
{
    public void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("WScript.Shell is unavailable: cannot create a shortcut");
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type)
                ?? throw new PlatformNotSupportedException("WScript.Shell did not start: cannot create a shortcut");
            dynamic link = ((dynamic)shell).CreateShortcut(shortcutPath);
            try
            {
                link.TargetPath = targetPath;
                link.Arguments = arguments;
                link.WorkingDirectory = workingDirectory;
                link.Description = description;
                link.IconLocation = targetPath + ",0";
                link.Save();
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
