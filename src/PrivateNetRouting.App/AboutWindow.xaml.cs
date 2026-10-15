using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using PrivateNetRouting.App.Localization;

namespace PrivateNetRouting.App;

/// <summary>
/// About: the application name and version, one line of what it is, and the project's site on
/// GitHub. Modal, holds no state — closed by the button or the title bar.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>The project's public page — a documented constant of the application itself,
    /// not a machine-specific value (rule #3).</summary>
    private const string HomePageUrl = "https://github.com/qcode-star/PrivateNetRouting";

    public AboutWindow()
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowTheme.UseDarkTitleBar(this);
        SiteLink.NavigateUri = new Uri(HomePageUrl, UriKind.Absolute);
        RenderTitles();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private void OnLanguageChanged() => RenderTitles();

    private void RenderTitles()
    {
        Title = Loc.S("Main.About.Title");
        HeaderText.Text = Loc.S("Main.About.Header");
        TaglineText.Text = Loc.S("Main.About.Tagline");
        VersionText.Text = Loc.F("Main.About.VersionFmt", AssemblyVersion());
        SiteRun.Text = HomePageUrl["https://".Length..];
        BtnClose.Content = Loc.S("Main.About.Close");
    }

    private static string AssemblyVersion() =>
        typeof(AboutWindow).Assembly.GetName().Version?.ToString() ?? "?";

    private void OnOpenSite(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(HomePageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no default browser registered: say so instead of dropping out of the click handler
            MessageBox.Show(this, ex.Message, Loc.S("App.Error.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
