using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrivateNetRouting.App;

/// <summary>
/// Shared application icon (Assets/app.ico, multi-size): one ImageSource for WPF windows
/// and one System.Drawing.Icon for the tray. Loaded once and reused.
/// </summary>
internal static class AppIcons
{
    private const string IconPackUri = "pack://application:,,,/Assets/app.ico";

    private static ImageSource? _windowIcon;

    private static System.Drawing.Icon? _trayIcon;

    public static ImageSource WindowIcon => _windowIcon ??= LoadWindowIcon();

    public static System.Drawing.Icon TrayIcon => _trayIcon ??= LoadTrayIcon();

    private static Stream OpenIconStream() =>
        System.Windows.Application.GetResourceStream(new Uri(IconPackUri)).Stream;

    private static ImageSource LoadWindowIcon()
    {
        using var stream = OpenIconStream();
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = OpenIconStream();
        return new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
    }
}
