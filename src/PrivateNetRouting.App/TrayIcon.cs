using PrivateNetRouting.App.Localization;
using WinForms = System.Windows.Forms;

namespace PrivateNetRouting.App;

/// <summary>
/// Notification area icon (tray): lives as long as the application; a double click and
/// the "Open" item show the main window, "Exit" is the only way to terminate
/// the application (closing the window only hides it to the tray). Menu items are localized
/// and rebound on language change (Loc.LanguageChanged).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string AppTitle = "PrivateNetRouting";

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _openItem;
    private readonly WinForms.ToolStripMenuItem _exitItem;
    private bool _disposed;

    /// <summary>The user asked to show the window (double click or menu).</summary>
    public event Action? OpenRequested;

    /// <summary>The user chose "Exit" — terminate the application entirely.</summary>
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _openItem = new WinForms.ToolStripMenuItem();
        _openItem.Click += (_, _) => OpenRequested?.Invoke();
        _exitItem = new WinForms.ToolStripMenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add(_openItem);
        _menu.Items.Add(_exitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = AppIcons.TrayIcon,
            Text = AppTitle,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        ApplyStrings();
        Loc.LanguageChanged += ApplyStrings;
    }

    /// <summary>A notification (balloon) on behalf of the application — for example, about a found update.</summary>
    public void ShowNotice(string text) =>
        _notifyIcon.ShowBalloonTip(10_000, AppTitle, text, WinForms.ToolTipIcon.Info);

    private void ApplyStrings()
    {
        _openItem.Text = Loc.S("Tray.Open");
        _exitItem.Text = Loc.S("Tray.Exit");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loc.LanguageChanged -= ApplyStrings;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
