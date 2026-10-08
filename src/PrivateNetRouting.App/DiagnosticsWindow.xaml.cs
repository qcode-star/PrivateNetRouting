using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.App;

/// <summary>
/// Diagnostics window, read-only: state summary (chain processes
/// and port owners, counters, activity marker, detailed CONNECT roundtrip, policy and ban,
/// watchdog) and log viewing — a list of files with the selected one's tail, rotation of the
/// live log by its owner (FileLog.RotateNow). The tail is refreshed by a timer, the summary by a button.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly ProxyRuntime _runtime;
    private readonly DispatcherTimer _tailTimer;
    private DiagnosticsSummary? _lastSummary;
    private bool _busy;
    private bool _fillingLogs;

    public DiagnosticsWindow(ProxyRuntime runtime)
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowTheme.UseDarkTitleBar(this);
        _runtime = runtime;
        Loc.LanguageChanged += OnLanguageChanged;
        RenderTitles();
        _tailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tailTimer.Tick += (_, _) => RefreshLogs();
        _tailTimer.Start();
        _ = RefreshAsync();
        RefreshLogs();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tailTimer.Stop();
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
        BtnOpenFolder.Content = Loc.S("Diag.Button.OpenFolder");
        BtnRotate.Content = Loc.S("Diag.Button.Rotate");
        LogsHeaderText.Text = Loc.S("Diag.Section.logs");
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
            _lastSummary = await _runtime.Diagnostics.GetSummaryAsync(CancellationToken.None);
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
            SectionsPanel.Children.Add(MakeLine(null, Loc.S("Diag.ChainStopped"), DiagMark.Neutral, wrap: true));
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
    /// a line without a label is a note in prose.</summary>
    private static TextBlock MakeLine(string? name, string value, DiagMark mark, bool wrap = false)
    {
        var block = new TextBlock
        {
            Style = (Style)Application.Current.FindResource(name is null ? "Text.Body" : "Text.Mono"),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
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

    /// <summary>List of log files preserving the selection; the tail — on selection and by timer.</summary>
    private void RefreshLogs()
    {
        var selected = (LogsList.SelectedItem as ListBoxItem)?.Tag as LogFileInfo;
        _fillingLogs = true;
        LogsList.Items.Clear();
        foreach (var file in _runtime.Logs.List())
        {
            LogsList.Items.Add(new ListBoxItem { Tag = file, Content = MakeLogRow(file) });
        }

        if (LogsList.Items.Count == 0)
        {
            LogsList.Items.Add(new ListBoxItem
            {
                IsEnabled = false,
                Content = new TextBlock
                {
                    Text = Loc.S("Diag.Logs.Empty"),
                    Style = (Style)FindResource("Text.Note"),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        var restore = LogsList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => item.Tag is LogFileInfo f && f.Name == selected?.Name)
            ?? LogsList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is not null);
        LogsList.SelectedItem = restore;
        _fillingLogs = false;
        ShowTail();
    }

    /// <summary>File row: the name, then size and time as the monospace metadata of the list.</summary>
    private Grid MakeLogRow(LogFileInfo file)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = file.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = file.Name,
        };
        var meta = new TextBlock
        {
            Text = $"{ByteSize.Format(file.Bytes)} · {file.LastWriteTime:HH:mm}",
            Style = (Style)FindResource("Text.Mono"),
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(meta, 1);
        row.Children.Add(name);
        row.Children.Add(meta);
        return row;
    }

    private void OnLogSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingLogs)
        {
            ShowTail();
        }
    }

    private void ShowTail()
    {
        if (LogsList.SelectedItem is not ListBoxItem { Tag: LogFileInfo info })
        {
            TailBox.Clear();
            return;
        }

        var tail = _runtime.Logs.ReadTail(info.Name, 200);
        TailBox.Text = tail is null ? "" : string.Join(Environment.NewLine, tail.Lines);
        TailBox.ScrollToEnd();
    }

    /// <summary>Rotation of the selected log: a process's live log — in place, another file — by shifting.</summary>
    private void OnRotateLog(object sender, RoutedEventArgs e)
    {
        if (LogsList.SelectedItem is not ListBoxItem { Tag: LogFileInfo info })
        {
            return;
        }

        var result = _runtime.RotateLog(info.Name);
        StatusText.Foreground = result.Ok ? Theme.TextSecondary : Theme.Bad;
        StatusText.Text = $"{info.Name}: {result.Detail}";
        RefreshLogs();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_runtime.Logs.Directory}\"") { UseShellExecute = true });

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
}
