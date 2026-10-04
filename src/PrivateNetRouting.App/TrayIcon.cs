using PrivateNetRouting.App.Localization;
using WinForms = System.Windows.Forms;

namespace PrivateNetRouting.App;

/// <summary>
/// Значок в области уведомлений (трей): живёт, пока живёт приложение; двойной клик и
/// пункт «Открыть» показывают главное окно, «Выход» — единственный способ завершить
/// приложение (закрытие окна только прячет его в трей). Пункты меню локализуются
/// и перевязываются при смене языка (Loc.LanguageChanged).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string AppTitle = "PrivateNetRouting";

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _openItem;
    private readonly WinForms.ToolStripMenuItem _exitItem;
    private bool _disposed;

    /// <summary>Пользователь попросил показать окно (двойной клик или меню).</summary>
    public event Action? OpenRequested;

    /// <summary>Пользователь выбрал «Выход» — завершить приложение целиком.</summary>
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

    /// <summary>Уведомление (балун) от имени приложения — например, о найденном обновлении.</summary>
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
