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
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, SectionsPanel.Children.Count == 0 ? 0 : 14, 0, 6),
            });
            foreach (var line in section.Lines)
            {
                SectionsPanel.Children.Add(MakeLine(line.Name, line.Value, line.Mark));
            }
        }
    }

    /// <summary>Summary line: a gray technical name and a value highlighted by mark.</summary>
    private static TextBlock MakeLine(string? name, string value, DiagMark mark, bool wrap = false)
    {
        var block = new TextBlock
        {
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 2),
            FontSize = 12.5,
        };
        if (name is not null)
        {
            block.Inlines.Add(new Run($"{name}:  ")
            {
                Foreground = (Brush)Application.Current.FindResource("Brush.TextSecondary"),
            });
        }

        block.Inlines.Add(new Run(value) { Foreground = BrushFor(mark) });
        return block;
    }

    private static Brush BrushFor(DiagMark mark) => mark switch
    {
        DiagMark.Good => new SolidColorBrush(Color.FromRgb(0x7C, 0xB9, 0x7C)),
        DiagMark.Warn => new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x3E)),
        DiagMark.Bad => new SolidColorBrush(Color.FromRgb(0xD9, 0x6B, 0x6B)),
        _ => (Brush)Application.Current.FindResource("Brush.Text"),
    };

    /// <summary>List of log files preserving the selection; the tail — on selection and by timer.</summary>
    private void RefreshLogs()
    {
        var selected = (LogsList.SelectedItem as ListBoxItem)?.Tag as LogFileInfo;
        _fillingLogs = true;
        LogsList.Items.Clear();
        foreach (var file in _runtime.Logs.List())
        {
            LogsList.Items.Add(new ListBoxItem
            {
                Tag = file,
                Content = $"{file.Name}  ·  {ByteSize.Format(file.Bytes)}  ·  {file.LastWriteTime:HH:mm}",
            });
        }

        var restore = LogsList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => item.Tag is LogFileInfo f && f.Name == selected?.Name)
            ?? LogsList.Items.OfType<ListBoxItem>().FirstOrDefault();
        LogsList.SelectedItem = restore;
        _fillingLogs = false;
        ShowTail();
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
        StatusText.Text = $"{info.Name}: {result.Detail}";
        RefreshLogs();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_runtime.Logs.Directory}\"") { UseShellExecute = true });

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
}
