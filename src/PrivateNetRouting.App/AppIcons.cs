using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.App;

/// <summary>
/// The application icon family (Assets/*.ico, multi-size): one ImageSource for the WPF windows and
/// one System.Drawing.Icon for the tray, chosen by the profile's variant — the neutral icon, the
/// Claude brand one, or the Codex brand one. Loaded once per variant and reused; the same file is
/// what the profile's desktop shortcuts point at.
/// </summary>
internal static class AppIcons
{
    private static AppIconVariant _variant = AppIconVariant.Both;

    private static ImageSource? _windowIcon;

    private static System.Drawing.Icon? _trayIcon;

    public static ImageSource WindowIcon => _windowIcon ??= LoadWindowIcon();

    public static System.Drawing.Icon TrayIcon => _trayIcon ??= LoadTrayIcon();

    /// <summary>
    /// Switches the window and tray icon to the profile's variant; the next access loads it. The
    /// windows created afterwards and the tray pick it up — the ones already open keep their icon.
    /// </summary>
    public static void Select(AppIconVariant variant)
    {
        if (_variant == variant)
        {
            return;
        }

        _variant = variant;
        _windowIcon = null;
        _trayIcon = null;
    }

    /// <summary>
    /// Gives the profile's desktop shortcuts the same icon. A shortcut's icon is a file, so the
    /// packaged variant is written out first; the neutral variant resets them to the exe's own icon.
    /// Best effort: an unreadable shortcut is logged and left alone, never a startup failure.
    /// </summary>
    public static void RefreshShortcuts(AppIconVariant variant, ProfileContext profile, FileLog log)
    {
        if (Environment.ProcessPath is not { } exePath)
        {
            return;
        }

        try
        {
            PublishShortcutIcon(variant);
            new ShortcutIconRefresher(new WScriptShortcutFactory(), log).Apply(exePath, profile, variant);
        }
        catch (Exception ex)
        {
            // the window and the tray already carry the variant; only the shortcuts are left behind
            log.Warn($"icons: the desktop shortcuts were not updated: {ex.Message}");
        }
    }

    /// <summary>Writes the variant's asset where the shortcuts look for it; an unchanged file is left as it is.</summary>
    private static void PublishShortcutIcon(AppIconVariant variant)
    {
        if (AppIconFiles.PathFor(variant) is not { } target)
        {
            return; // the exe's own icon is the right one
        }

        byte[] bytes;
        using (var stream = OpenIconStream(variant))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        Directory.CreateDirectory(AppIconFiles.Directory);
        var temp = $"{target}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The packaged asset of the variant; the shortcut file name is the same (AppIconFiles).</summary>
    private static string AssetName(AppIconVariant variant) =>
        AppIconFiles.PathFor(variant) is { } path ? Path.GetFileName(path) : "app.ico";

    private static Stream OpenIconStream(AppIconVariant variant) =>
        System.Windows.Application.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/{AssetName(variant)}")).Stream;

    private static ImageSource LoadWindowIcon()
    {
        using var stream = OpenIconStream(_variant);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = OpenIconStream(_variant);
        return new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
    }
}
