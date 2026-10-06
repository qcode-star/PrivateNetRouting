using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PrivateNetRouting.App;

/// <summary>
/// Dark window chrome. The title bar is drawn by DWM and follows the system theme, which would put
/// a light bar on top of a dark window on a light-mode system. The attribute below asks DWM for the
/// dark variant; a build that does not know it answers with an error code, and the window then
/// simply keeps the system bar — that is not worth failing a launch over.
/// </summary>
internal static class WindowTheme
{
    private const int DarkModeAttribute = 20;

    private const int DarkModeAttributePre20H1 = 19;

    public static void UseDarkTitleBar(Window window) => window.SourceInitialized += (_, _) => Apply(window);

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, DarkModeAttribute, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DarkModeAttributePre20H1, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
