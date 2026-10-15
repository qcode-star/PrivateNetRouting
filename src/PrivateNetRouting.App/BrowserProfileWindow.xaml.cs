using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.App;

/// <summary>
/// Creation of the browser profile (the "?" help menu): the family, the profile directory and the
/// desktop shortcut. The window only collects the answers and says what will happen — the profile
/// itself is written by BrowserProfileService. Modal: the profile is either created or cancelled.
/// </summary>
public partial class BrowserProfileWindow : Window
{
    private readonly AppConfig _config;
    private readonly BrowserProfileService _profiles;
    private readonly FileLog _log;
    private readonly Dictionary<BrowserKind, string> _manualExecutables = [];
    private readonly Dictionary<BrowserKind, string?> _detectedExecutables = [];
    private bool _folderTouched;
    private bool _shortcutTouched;
    private bool _loading = true;
    private bool _refreshing;

    /// <summary>What was created; null while the dialog was not finished with "Create".</summary>
    public BrowserProfileResult? Result { get; private set; }

    public BrowserProfileWindow(AppConfig config, BrowserProfileService profiles, FileLog log)
    {
        _config = config;
        _profiles = profiles;
        _log = log;
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowTheme.UseDarkTitleBar(this);
        Loc.LanguageChanged += OnLanguageChanged;
        RenderTexts();
        Prefill();
        _loading = false;
        RefreshState(kindChanged: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private BrowserKind SelectedKind => KindChrome.IsChecked == true ? BrowserKind.Chrome : BrowserKind.Firefox;

    private void OnLanguageChanged()
    {
        RenderTexts();
        RefreshState(kindChanged: false);
    }

    private void RenderTexts()
    {
        Title = Loc.S("BrowserProfile.Title");
        HeaderText.Text = Loc.S("BrowserProfile.Header");
        IntroText.Text = Loc.S("BrowserProfile.Intro");
        KindLabel.Text = Loc.S("BrowserProfile.Kind");
        KindFirefox.Content = Loc.S("BrowserProfile.Kind.Firefox");
        KindChrome.Content = Loc.S("BrowserProfile.Kind.Chrome");
        FolderLabel.Text = Loc.S("BrowserProfile.Folder");
        BrowseButton.Content = Loc.S("BrowserProfile.Browse");
        ExeLabel.Text = Loc.S("BrowserProfile.Executable");
        SpecifyExeButton.Content = Loc.S("BrowserProfile.Specify");
        ShortcutCheck.Content = Loc.S("BrowserProfile.Shortcut");
        BrowseShortcutButton.Content = Loc.S("BrowserProfile.Browse");
        System.Windows.Automation.AutomationProperties.SetName(ShortcutBox, Loc.S("BrowserProfile.Shortcut"));
        CancelButton.Content = Loc.S("BrowserProfile.Cancel");
        CreateButton.Content = Loc.S("BrowserProfile.Create");
        ExistingText.Text = _config.Browser.IsConfigured
            ? Loc.F(
                "BrowserProfile.ExistingFmt",
                BrowserKindParser.DisplayName(_config.Browser.Kind ?? BrowserKind.Firefox),
                _config.Browser.ProfileDir)
            : "";
        ExistingText.Visibility = _config.Browser.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Current values of the config, or the detected family with its suggested folder.
    /// A half-filled config (a family recorded without paths — an old scheme's section, say) is
    /// treated as not configured: the owner gets the suggestions instead of an empty form.
    /// </summary>
    private void Prefill()
    {
        _loading = true;
        if (_config.Browser.IsConfigured && _config.Browser.Kind is { } configured)
        {
            if (configured == BrowserKind.Firefox)
            {
                KindFirefox.IsChecked = true;
            }
            else
            {
                KindChrome.IsChecked = true;
            }

            FolderBox.Text = _config.Browser.ProfileDir;
            ShortcutCheck.IsChecked = _config.Browser.ShortcutPath.Length > 0;
            if (_config.Browser.ShortcutPath.Length > 0)
            {
                ShortcutBox.Text = _config.Browser.ShortcutPath;
            }

            _folderTouched = true;
            _shortcutTouched = ShortcutBox.Text.Length > 0;
            if (_config.Browser.ExecutablePath.Length > 0)
            {
                _manualExecutables[configured] = _config.Browser.ExecutablePath;
            }
        }
        else
        {
            // a family named by the config (an old scheme's hint) or the first one installed
            var kind = _config.Browser.Kind
                ?? (_profiles.FindExecutable(BrowserKind.Firefox) is not null
                    ? BrowserKind.Firefox
                    : BrowserKind.Chrome);
            if (kind == BrowserKind.Firefox)
            {
                KindFirefox.IsChecked = true;
            }
            else
            {
                KindChrome.IsChecked = true;
            }

            FolderBox.Text = BrowserProfileService.SuggestProfileDir(kind);
            ShortcutBox.Text = _profiles.ShortcutPathFor(kind);
            ShortcutCheck.IsChecked = true;
        }

        _loading = false;
    }

    private void OnKindChanged(object sender, RoutedEventArgs e) => RefreshState(kindChanged: true);

    // a change that is not the window's own (loading the config, a suggestion) is the user's: typed
    // and picked paths alike must survive a later switch of the browser family
    private void OnFolderChanged(object sender, TextChangedEventArgs e)
    {
        _folderTouched |= !_loading && !_refreshing;
        RefreshState(kindChanged: false);
    }

    private void OnShortcutToggled(object sender, RoutedEventArgs e) => RefreshState(kindChanged: false);

    private void OnShortcutPathChanged(object sender, TextChangedEventArgs e)
    {
        _shortcutTouched |= !_loading && !_refreshing;
        RefreshState(kindChanged: false);
    }

    /// <summary>Everything the window shows about the future profile is recomputed here.</summary>
    private void RefreshState(bool kindChanged)
    {
        if (_loading || _refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var kind = SelectedKind;
            if (kindChanged && !_folderTouched)
            {
                FolderBox.Text = BrowserProfileService.SuggestProfileDir(kind);
            }

            if (kindChanged && !_shortcutTouched)
            {
                ShortcutBox.Text = _profiles.ShortcutPathFor(kind);
            }

            var shortcutOn = ShortcutCheck.IsChecked == true;
            if (shortcutOn && !_shortcutTouched && ShortcutBox.Text.Trim().Length == 0)
            {
                // a configured profile without a shortcut: checking the box suggests the default
                ShortcutBox.Text = _profiles.ShortcutPathFor(kind);
            }

            var exe = ExecutableFor(kind);
            ExeText.Text = exe ?? Loc.S("BrowserProfile.ExeNone");
            SetText(ExeWarning, exe is null ? Loc.S("BrowserProfile.ExeMissing") : null);

            ShortcutPathRow.Visibility = shortcutOn ? Visibility.Visible : Visibility.Collapsed;
            var (folderBlocking, advisory) = InspectFolder(kind);
            var shortcutBlocking = shortcutOn ? InspectShortcut() : null;
            var blocking = folderBlocking ?? shortcutBlocking;
            SetText(StatusText, blocking ?? advisory);
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                blocking is not null ? "Brush.Bad" : "Brush.Warn");
            CreateButton.IsEnabled = blocking is null && exe is not null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void SetText(TextBlock target, string? text)
    {
        target.Text = text ?? "";
        target.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? ExecutableFor(BrowserKind kind)
    {
        if (_manualExecutables.TryGetValue(kind, out var manual))
        {
            return manual;
        }

        if (_detectedExecutables.TryGetValue(kind, out var detected) && detected is not null)
        {
            return detected;
        }

        var found = _profiles.FindExecutable(kind);
        if (found is not null)
        {
            _detectedExecutables[kind] = found;
        }

        return found;
    }

    /// <summary>
    /// Blocking issue of the folder (the profile must not be created there) and an advisory one
    /// (worth a confirmation: the profile files would land next to someone else's).
    /// </summary>
    private (string? Blocking, string? Advisory) InspectFolder(BrowserKind kind)
    {
        var text = FolderBox.Text.Trim();
        if (text.Length == 0)
        {
            return (Loc.S("BrowserProfile.FolderRequired"), null);
        }

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(text));
        }
        catch (Exception ex)
        {
            return (ex.Message, null);
        }

        if (File.Exists(full))
        {
            return (Loc.F("BrowserProfile.FolderIsFileFmt", full), null);
        }

        if (LooksLikeOurProfile(full, kind))
        {
            return (null, null);
        }

        try
        {
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            {
                return (null, Loc.F("BrowserProfile.FolderNotEmptyFmt", full));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // this runs on every keystroke of the folder box: a folder we cannot read is a
            // reason to refuse it, not an exception out of a TextChanged handler
            return (ex.Message, null);
        }

        return (null, null);
    }

    /// <summary>A profile we write ourselves (or the configured one): re-creating it asks nothing.</summary>
    private bool LooksLikeOurProfile(string full, BrowserKind kind)
    {
        if (full.Equals(_config.Browser.ProfileDir, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return File.Exists(Path.Combine(full, BrowserProfileFiles.FirefoxUserJsName))
            || Directory.Exists(Path.Combine(full, "Default"));
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.S("BrowserProfile.FolderPickTitle"),
            Multiselect = false,
        };
        var current = FolderBox.Text.Trim();
        if (current.Length > 0)
        {
            var full = SafeFullPath(current);
            if (!string.IsNullOrEmpty(full))
            {
                dialog.InitialDirectory = Directory.Exists(full)
                    ? full
                    : Path.GetDirectoryName(full) ?? "";
            }
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        FolderBox.Text = dialog.FolderName;
        _folderTouched = true;
    }

    private void OnSpecifyExecutable(object sender, RoutedEventArgs e)
    {
        var kind = SelectedKind;
        var current = ExecutableFor(kind);
        var dialog = new OpenFileDialog
        {
            Title = Loc.S("BrowserProfile.ExePickTitle"),
            Filter = Loc.S("BrowserProfile.ExeFilter"),
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(current))
        {
            dialog.FileName = Path.GetFileName(current);
            dialog.InitialDirectory = Path.GetDirectoryName(current) ?? "";
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _manualExecutables[kind] = dialog.FileName;
        RefreshState(kindChanged: false);
    }

    /// <summary>Blocking issue of the shortcut path (the caller checks the checkbox first).</summary>
    private string? InspectShortcut()
    {
        var text = ShortcutBox.Text.Trim();
        if (text.Length == 0)
        {
            return Loc.S("BrowserProfile.ShortcutRequired");
        }

        // the shell shortcut factory is bound to the .lnk association: any other extension
        // would produce a file Windows cannot follow
        if (!string.Equals(Path.GetExtension(text), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("BrowserProfile.ShortcutExtensionFmt", text);
        }

        return null;
    }

    private void OnBrowseShortcut(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.S("BrowserProfile.ShortcutPickTitle"),
            Filter = Loc.S("BrowserProfile.ShortcutFilter"),
            AddExtension = true,
            DefaultExt = ".lnk",
        };
        var current = ShortcutBox.Text.Trim();
        if (current.Length > 0)
        {
            dialog.FileName = Path.GetFileName(current);
            dialog.InitialDirectory = SafeFullPath(Path.GetDirectoryName(current) ?? "") ?? "";
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ShortcutBox.Text = dialog.FileName;
        _shortcutTouched = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var kind = SelectedKind;
        var exe = ExecutableFor(kind);
        var (blocking, advisory) = InspectFolder(kind);
        if (exe is null || blocking is not null)
        {
            return; // the button is disabled in these states; a race is simply ignored
        }

        if (advisory is not null && MessageBox.Show(
                this,
                advisory,
                Loc.S("BrowserProfile.ConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Result = _profiles.Create(new BrowserProfileRequest(
                kind,
                FolderBox.Text.Trim(),
                exe,
                ShortcutCheck.IsChecked == true,
                ShortcutCheck.IsChecked == true ? ShortcutBox.Text.Trim() : ""));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _log.Warn($"browser profile: creation failed: {ex.Message}");
            MessageBox.Show(
                this,
                Loc.F("Main.ErrorFmt", ex.Message),
                Loc.S("BrowserProfile.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
