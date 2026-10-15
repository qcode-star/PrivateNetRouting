using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.App;

/// <summary>
/// Diagnostics window, read-only: the state summary (chain processes and port owners, counters,
/// activity marker, detailed CONNECT roundtrip, policy and ban, watchdog), rebuilt on demand —
/// every probe is live traffic. The log files themselves are one click away: the button opens
/// their folder in Explorer; the log list and rotation live in the CLI (pnr logs).
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly ProxyRuntime _runtime;
    private DiagnosticsSummary? _lastSummary;
    private bool _busy;

    public DiagnosticsWindow(ProxyRuntime runtime)
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowTheme.UseDarkTitleBar(this);
        _runtime = runtime;
        Loc.LanguageChanged += OnLanguageChanged;
        RenderTitles();
        _ = RefreshAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private void OnLanguageChanged()
    {
        RenderTitles();
        if (_lastSummary is not null)
        {
            RenderSummary(_lastSummary);
        }
    }

    private void RenderTitles()
    {
        Title = Loc.S("Diag.Title");
        HeaderText.Text = Loc.S("Diag.Header");
        BtnRefresh.Content = Loc.S("Diag.Button.Refresh");
        BtnOpenLogs.Content = Loc.S("Diag.Button.OpenLogs");
    }

    /// <summary>The summary is rebuilt on demand and on language change: every probe is live traffic.</summary>
    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        StatusText.Foreground = Theme.TextSecondary;
        StatusText.Text = Loc.S("Diag.Busy");
        BtnRefresh.IsEnabled = false;
        try
        {
            _lastSummary = await Task.Run(() => _runtime.Diagnostics.GetSummaryAsync(CancellationToken.None));
            RenderSummary(_lastSummary);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = Theme.Bad;
            StatusText.Text = Loc.F("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _busy = false;
            BtnRefresh.IsEnabled = true;
        }
    }

    private void RenderSummary(DiagnosticsSummary summary)
    {
        SectionsPanel.Children.Clear();
        if (summary.ChainStopped)
        {
            SectionsPanel.Children.Add(MakeLine(null, Loc.S("Diag.ChainStopped"), DiagMark.Neutral));
            return;
        }

        foreach (var section in summary.Sections)
        {
            SectionsPanel.Children.Add(new TextBlock
            {
                Text = Loc.S($"Diag.Section.{section.Key}"),
                Style = (Style)Application.Current.FindResource("Text.Section"),
                Margin = new Thickness(0, SectionsPanel.Children.Count == 0 ? 0 : 18, 0, 8),
            });
            foreach (var line in section.Lines)
            {
                SectionsPanel.Children.Add(MakeLine(line.Name, line.Value, line.Mark));
            }
        }
    }

    /// <summary>Summary line: a muted label and a monospace value carrying the mark of its state;
    /// a line without a label is a note in prose. Lines wrap, so long values stay readable.</summary>
    private static TextBlock MakeLine(string? name, string value, DiagMark mark)
    {
        var block = new TextBlock
        {
            Style = (Style)Application.Current.FindResource(name is null ? "Text.Body" : "Text.Mono"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 3),
        };
        if (name is not null)
        {
            block.Inlines.Add(new Run($"{name}:  ")
            {
                Foreground = Theme.TextMuted,
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui"),
            });
        }

        block.Inlines.Add(new Run(value) { Foreground = BrushFor(mark) });
        return block;
    }

    private static Brush BrushFor(DiagMark mark) => mark switch
    {
        DiagMark.Good => Theme.Ok,
        DiagMark.Warn => Theme.Warn,
        DiagMark.Bad => Theme.Bad,
        _ => Theme.Text,
    };

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_runtime.Logs.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Foreground = Theme.Bad;
            StatusText.Text = Loc.F("Main.ErrorFmt", ex.Message);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
}
