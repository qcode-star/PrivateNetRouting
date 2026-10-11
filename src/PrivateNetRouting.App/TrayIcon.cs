using PrivateNetRouting.App.Localization;
using WinForms = System.Windows.Forms;

namespace PrivateNetRouting.App;

/// <summary>
/// Notification area icon (tray): lives as long as the application. A double click and the
/// "Show window" item show the main window; the chain items run the same operations as the
/// chain card (without showing the window, enablement follows the card's buttons); "Launch
/// Claude" is the same as the window's launch button; the two exit items differ in the chain:
/// "Exit" leaves it running in the background (the next GUI start sees it), "Exit with full
/// chain stop" brings it down first. Closing the window only hides it to the tray — exit is
/// only from this menu. Menu items are localized and rebound on language change
/// (Loc.LanguageChanged).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string AppTitle = "PrivateNetRouting";

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _openItem;
    private readonly WinForms.ToolStripMenuItem _chainUpItem;
    private readonly WinForms.ToolStripMenuItem _chainDownItem;
    private readonly WinForms.ToolStripMenuItem _launchClaudeItem;
    private readonly WinForms.ToolStripMenuItem _exitItem;
    private readonly WinForms.ToolStripMenuItem _exitStopItem;
    private bool _disposed;

    /// <summary>The user asked to show the window (double click or menu).</summary>
    public event Action? OpenRequested;

    /// <summary>The user asked to bring the chain up (the same as the chain card button).</summary>
    public event Action? ChainUpRequested;

    /// <summary>The user asked to stop the chain (the same as the chain card button).</summary>
    public event Action? ChainDownRequested;

    /// <summary>The user asked to launch Claude (the same as the window's launch button).</summary>
    public event Action? LaunchClaudeRequested;

    /// <summary>The user chose "Exit" — terminate the application, the chain keeps running.</summary>
    public event Action? ExitRequested;

    /// <summary>The user chose "Exit with full chain stop" — bring the chain down, then terminate.</summary>
    public event Action? ExitAndStopChainRequested;

    public TrayIcon()
    {
        _openItem = new WinForms.ToolStripMenuItem();
        _openItem.Click += (_, _) => OpenRequested?.Invoke();
        _chainUpItem = new WinForms.ToolStripMenuItem();
        _chainUpItem.Click += (_, _) => ChainUpRequested?.Invoke();
        _chainDownItem = new WinForms.ToolStripMenuItem();
        _chainDownItem.Click += (_, _) => ChainDownRequested?.Invoke();
        _launchClaudeItem = new WinForms.ToolStripMenuItem();
        _launchClaudeItem.Click += (_, _) => LaunchClaudeRequested?.Invoke();
        _exitItem = new WinForms.ToolStripMenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _exitStopItem = new WinForms.ToolStripMenuItem();
        _exitStopItem.Click += (_, _) => ExitAndStopChainRequested?.Invoke();
        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add(_openItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_chainUpItem);
        _menu.Items.Add(_chainDownItem);
        _menu.Items.Add(_launchClaudeItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        _menu.Items.Add(_exitStopItem);

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

    /// <summary>
    /// Enablement of the action items, straight from the main window's view model (the same
    /// Can* the card's buttons obey); the exits are always available.
    /// </summary>
    public void UpdateActions(bool canChainUp, bool canChainDown, bool canLaunchClaude)
    {
        _chainUpItem.Enabled = canChainUp;
        _chainDownItem.Enabled = canChainDown;
        _launchClaudeItem.Enabled = canLaunchClaude;
    }

    /// <summary>Claude turned off in the config (apps.claude=false) — the launch item leaves the menu.</summary>
    public void ShowLaunchClaude(bool visible) => _launchClaudeItem.Visible = visible;

    private void ApplyStrings()
    {
        _openItem.Text = Loc.S("Tray.Open");
        _chainUpItem.Text = Loc.S("Tray.ChainUp");
        _chainDownItem.Text = Loc.S("Tray.ChainDown");
        _launchClaudeItem.Text = Loc.S("Tray.LaunchClaude");
        _exitItem.Text = Loc.S("Tray.Exit");
        _exitStopItem.Text = Loc.S("Tray.ExitStop");
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
