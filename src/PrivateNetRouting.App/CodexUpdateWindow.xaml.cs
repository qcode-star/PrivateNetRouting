using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.App;

/// <summary>
/// Codex desktop app update window (Microsoft Store): steps "Check → Download → Install".
/// The check asks the Store catalog (no chain, no UAC); the update hands the package to the
/// Store and follows its progress — the Store downloads over its own CDN and registers the
/// package itself. A running Codex is closed first (with a confirmation). "Stop" cancels the
/// Store update while it is still moving; a finished check and a finished installation keep
/// the main window's update dot in sync through VersionStateResolved.
/// </summary>
public partial class CodexUpdateWindow : Window
{
    /// <summary>How long the window lives after a successful installation.</summary>
    private const int AutoCloseSeconds = 30;

    private static readonly string[] StepOrder = ["Check", "Download", "Install"];

    private readonly ProxyRuntime _runtime;
    private readonly string _logPath;
    private readonly StepRailRow[] _rows;
    private readonly ChainStepStatus[] _states;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _autoClose;
    private readonly Stopwatch _watch = new();

    private int _autoCloseLeft;
    private CancellationTokenSource? _cts;
    private IStoreInstallHandle? _installHandle;
    private string? _beforeVersion;
    private bool _terminal;
    private bool _installRegistered;
    private DateTimeOffset _detailRefreshAt;
    private string? _detailKey;
    private object?[] _detailArgs = [];
    private string _detailDirect = "";
    private string? _hintKey;
    private object?[] _hintArgs = [];
    private string _hintDirect = "";

    public CodexUpdateWindow(ProxyRuntime runtime)
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowTheme.UseDarkTitleBar(this);
        // the height belongs to the content; the cap keeps a short screen from growing the window off the work area
        MaxHeight = SystemParameters.WorkArea.Height;
        _runtime = runtime;
        _logPath = runtime.CodexUpdater.Log.FilePath;

        _rows = new StepRailRow[StepOrder.Length];
        _states = new ChainStepStatus[StepOrder.Length];
        var body = (Style)FindResource("Text.Body");
        var mono = (Style)FindResource("Text.Mono");
        for (var i = 0; i < StepOrder.Length; i++)
        {
            _states[i] = ChainStepStatus.Pending;
            _rows[i] = new StepRailRow(Loc.S($"CodexUpdate.Step.{StepOrder[i]}"), body, mono);
            _rows[i].Apply(ChainStepStatus.Pending, "");
            StepsPanel.Children.Add(_rows[i].Element);
        }

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _clock.Tick += (_, _) => ElapsedText.Text = $"{(int)_watch.Elapsed.TotalMinutes}:{_watch.Elapsed.Seconds:00}";

        // the countdown after a successful installation: any click or key press keeps the window
        // open. A press on a button is exempt from the preview cancel: hiding the countdown line
        // here would shrink the window (SizeToContent=Height) and the button would slip away from
        // under the cursor before the release — the click would miss. Buttons cancel the
        // countdown through their bubbling Click instead, after the click has already landed.
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoClose.Tick += (_, _) => OnAutoCloseTick();
        PreviewMouseDown += (_, e) =>
        {
            if (!IsWithinButton(e.OriginalSource))
            {
                CancelAutoClose();
            }
        };
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => CancelAutoClose()));
        PreviewKeyDown += (_, _) => CancelAutoClose();

        Closed += (_, _) =>
        {
            Loc.LanguageChanged -= OnLanguageChanged;
            _clock.Stop();
            _autoClose.Stop();
        };
        Loc.LanguageChanged += OnLanguageChanged;
        ApplyStrings();
        Loaded += async (_, _) =>
        {
            if (_cts is null && !_terminal)
            {
                await StartAsync();
            }
        };
    }

    /// <summary>
    /// The version state the window has just resolved — by its own check or by a finished
    /// installation. The caller keeps its "update available" dot in sync with it.
    /// </summary>
    public event Action<CodexUpdateCheck>? VersionStateResolved;

    /// <summary>Full cycle: check → Store download → Store install.</summary>
    private async Task StartAsync()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _terminal = false;
        _installRegistered = false;
        _beforeVersion = null;
        _watch.Restart();
        _clock.Start();
        ElapsedText.Text = "0:00";
        ShowProgress(true);
        SetProgressUnknown();
        BtnStop.Visibility = Visibility.Visible;
        BtnRetry.Visibility = Visibility.Collapsed;
        BtnLaunch.Visibility = Visibility.Collapsed;
        SetHintDirect("");
        for (var i = 0; i < StepOrder.Length; i++)
        {
            SetStep(i, ChainStepStatus.Pending);
        }

        try
        {
            // The Store catalog check: no chain, no UAC — the Store CDN is not part of the ban
            SetStep(0, ChainStepStatus.Active);
            SetDetailKey("CodexUpdate.Check.Running");
            var check = await _runtime.CodexUpdater.CheckAsync(ct);
            VersionStateResolved?.Invoke(check);
            if (check.StoreUnavailable)
            {
                SetDetailKey("CodexUpdate.Check.StoreUnavailable");
                Finish(success: false);
                return;
            }

            _beforeVersion = check.Installed.Version;
            SetStep(0, ChainStepStatus.Done, check.Installed.Version);
            if (!check.UpdateAvailable)
            {
                for (var i = 1; i < StepOrder.Length; i++)
                {
                    SetStep(i, ChainStepStatus.Skipped);
                }

                SetDetailKey("CodexUpdate.Check.UpToDateFmt", check.Installed.Version);
                Finish(success: true);
                return;
            }

            if (check.OfferedVersion is not null)
            {
                SetDetailKey("CodexUpdate.Check.OfferedFmt", check.Installed.Version, check.OfferedVersion);
            }
            else
            {
                SetDetailKey("CodexUpdate.Check.AvailableFmt", check.Installed.Version);
            }

            // The app holds the old package registration — it must go before the Store can replace it
            if (await Task.Run(() => _runtime.CodexUpdater.IsCodexRunning()))
            {
                var answer = MessageBox.Show(
                    this,
                    Loc.S("CodexUpdate.ConfirmClose"),
                    Loc.S("CodexUpdate.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    SetDetailKey("CodexUpdate.Done.Declined");
                    Finish(success: true);
                    return;
                }
            }

            SetStep(1, ChainStepStatus.Active);
            var after = await _runtime.CodexUpdater.UpdateAsync(
                new Progress<UpdateProgress>(OnUpdateProgress),
                ct,
                handle => _installHandle = handle);
            // the poll walk lights Download itself; make sure both are settled
            SetStep(1, ChainStepStatus.Done);
            SetStep(2, ChainStepStatus.Done, after.Version);
            SetDetailKey("CodexUpdate.Done.InstalledFmt", _beforeVersion, after.Version);
            if (after.Version == _beforeVersion)
            {
                SetHintKey("CodexUpdate.Install.VersionWarn");
            }
            else
            {
                // the update is installed — no "update available" is left
                VersionStateResolved?.Invoke(new CodexUpdateCheck(after, UpdateAvailable: false, StoreUnavailable: false));
            }

            Finish(success: true, showLaunch: true, progressFull: true);
            StartAutoClose();
        }
        catch (OperationCanceledException)
        {
            SetDetailKey("Update.Done.Stopped");
            Finish(success: false);
        }
        catch (Exception ex)
        {
            MarkCurrentStepFailed();
            SetDetailDirect(ex.Message);
            Finish(success: false);
        }
    }

    private void OnUpdateProgress(UpdateProgress p)
    {
        if (p.Phase == UpdatePhase.Download && !_installRegistered)
        {
            SetStep(1, ChainStepStatus.Active);
            if (p.TotalBytes > 0)
            {
                SetProgress(100.0 * p.DoneBytes / p.TotalBytes);

                // the line repaints about once a second: the bar moves as the data arrives,
                // but the numbers stay readable instead of flickering at the report rate
                var now = DateTimeOffset.Now;
                if (now - _detailRefreshAt >= TimeSpan.FromSeconds(1))
                {
                    _detailRefreshAt = now;
                    SetDetailKey(
                        "Update.Download.RunningFmt",
                        FormattedRuns.Pad(ByteSize.Format(p.DoneBytes)),
                        FormattedRuns.Pad(ByteSize.Format(p.TotalBytes)),
                        FormattedRuns.Pad("—"),
                        FormattedRuns.Pad("—"));
                }
            }
        }
        else if (p.Phase == UpdatePhase.Install)
        {
            if (!_installRegistered)
            {
                _installRegistered = true;
                SetStep(1, ChainStepStatus.Done);
                SetStep(2, ChainStepStatus.Active);
            }

            SetProgressUnknown();
            SetDetailDirect(p.Message);
        }
        else if (p.Phase is UpdatePhase.Done or UpdatePhase.Check && p.Message.Length > 0)
        {
            SetDetailDirect(p.Message);
        }
    }

    private void SetStep(int index, ChainStepStatus status, string detail = "")
    {
        _states[index] = status;
        _rows[index].Apply(status, detail);
        for (var i = 1; i < StepOrder.Length; i++)
        {
            _rows[i].SetLinkFrom(_states[i - 1]);
        }
    }

    private void MarkCurrentStepFailed()
    {
        // Check is index 0; the update itself (download or install) fails step 1 while it is the
        // active one — the install step only after the poll handed the work over to it
        var index = _states[2] == ChainStepStatus.Active ? 2
            : _states[1] is ChainStepStatus.Active or ChainStepStatus.Pending ? 1
            : 0;
        if (_states[index] is ChainStepStatus.Pending or ChainStepStatus.Active)
        {
            SetStep(index, ChainStepStatus.Failed);
        }
    }

    /// <summary>Terminal state: the clock freezes, "Stop" disappears, "Retry"/"Launch Codex" appear.</summary>
    private void Finish(bool success, bool showLaunch = false, bool progressFull = false)
    {
        _terminal = true;
        _clock.Stop();
        _installHandle = null;
        if (success && progressFull)
        {
            SetProgress(100);
        }
        else
        {
            ShowProgress(false);
            SetProgressUnknown();
        }

        BtnStop.Visibility = Visibility.Collapsed;
        BtnRetry.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        BtnLaunch.Visibility = showLaunch ? Visibility.Visible : Visibility.Collapsed;
        BtnClose.IsEnabled = true;
    }

    /// <summary>
    /// After a real installation the window leaves on its own: the countdown is visible from the
    /// first second, and any interaction means the user still wants it — see CancelAutoClose.
    /// </summary>
    private void StartAutoClose()
    {
        _autoCloseLeft = AutoCloseSeconds;
        _autoClose.Start();
        RefreshCountdown();
    }

    private void CancelAutoClose()
    {
        if (!_autoClose.IsEnabled)
        {
            return;
        }

        _autoClose.Stop();
        RefreshCountdown();
    }

    /// <summary>Whether the hit-tested element sits on a button — the press belongs to it, not to the window.</summary>
    private static bool IsWithinButton(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnAutoCloseTick()
    {
        _autoCloseLeft--;
        if (_autoCloseLeft <= 0)
        {
            _autoClose.Stop();
            Close();
            return;
        }

        RefreshCountdown();
    }

    private void RefreshCountdown()
    {
        CountdownText.Visibility = _autoClose.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_autoClose.IsEnabled)
        {
            CountdownText.Inlines.Clear();
            CountdownText.Inlines.AddRange(FormattedRuns.Build("Update.AutoCloseFmt", FormattedRuns.Pad(_autoCloseLeft, 2)));
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _installHandle?.Cancel();
        _cts?.Cancel();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = StartAsync();

    private async void OnLaunchCodex(object sender, RoutedEventArgs e)
    {
        BtnLaunch.IsEnabled = false;
        try
        {
            // the same contract as the main window's button: a copy that runs without the proxy is
            // offered a restart (its flags never reach a running single-instance app otherwise)
            var running = await Task.Run(() => _runtime.CodexLauncher.InspectRunning());
            if (!LaunchPrompts.ConfirmRestart(this, running, "App.Name.Codex"))
            {
                _runtime.CodexLauncher.NoteRestartDeclined();
                SetHintKey("Main.Launch.RestartDeclined");
                return;
            }

            // the launcher itself will check/bring up the chain and pass the proxy flags and env
            await _runtime.CodexLauncher.LaunchAsync(LaunchPrompts.OptionsFor(running), null, CancellationToken.None);
            SetHintKey("CodexUpdate.Launch.Done");
        }
        catch (Exception ex)
        {
            SetHintDirect(ex.Message);
        }
        finally
        {
            BtnLaunch.IsEnabled = true;
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_logPath))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_logPath}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            SetHintDirect(ex.Message);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_terminal)
        {
            // the Store is registering the new package (the install step) — closing now would
            // leave the registration half-observed; a download can be cancelled like in the Claude window
            if (_states[2] == ChainStepStatus.Active)
            {
                e.Cancel = true;
                SetHintKey("Update.Install.NoClose");
                return;
            }

            if (_states[1] == ChainStepStatus.Active)
            {
                var answer = MessageBox.Show(
                    this,
                    Loc.S("Update.ConfirmClose"),
                    Loc.S("CodexUpdate.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _cts?.Cancel();
        }

        base.OnClosing(e);
    }

    /// <summary>Progress area: the bar together with the percentage that goes with it.</summary>
    private void ShowProgress(bool visible) =>
        ProgressArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Known length: a determinate bar and the number the user actually reads.</summary>
    private void SetProgress(double percent)
    {
        Progress.IsIndeterminate = false;
        Progress.Value = percent;
        ProgressText.Text = $"{percent:0} %";
    }

    /// <summary>Unknown length: the bar breathes and the number stays empty instead of inventing one.</summary>
    private void SetProgressUnknown()
    {
        Progress.IsIndeterminate = true;
        ProgressText.Text = "";
    }

    private void SetDetailKey(string key, params object?[] args)
    {
        _detailKey = key;
        _detailArgs = args;
        _detailDirect = "";
        RefreshDetail();
    }

    private void SetDetailDirect(string text)
    {
        _detailKey = null;
        _detailDirect = text;
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        DetailText.Inlines.Clear();
        if (_detailKey is not null)
        {
            DetailText.Inlines.AddRange(FormattedRuns.Build(_detailKey, _detailArgs));
        }
        else
        {
            DetailText.Inlines.Add(new Run(_detailDirect));
        }
    }

    private void SetHintKey(string key, params object?[] args)
    {
        _hintKey = key;
        _hintArgs = args;
        _hintDirect = "";
        RefreshHint();
    }

    private void SetHintDirect(string text)
    {
        _hintKey = null;
        _hintDirect = text;
        RefreshHint();
    }

    private void RefreshHint() => HintText.Text = _hintKey is not null ? Loc.F(_hintKey, _hintArgs) : _hintDirect;

    private void ApplyStrings()
    {
        Title = Loc.S("CodexUpdate.Title");
        HeaderText.Text = Loc.S("CodexUpdate.Header");
        SubHeaderText.Text = Loc.S("CodexUpdate.SubHeader");
        for (var i = 0; i < StepOrder.Length; i++)
        {
            _rows[i].Label.Text = Loc.S($"CodexUpdate.Step.{StepOrder[i]}");
        }

        BtnStop.Content = Loc.S("Update.Button.Stop");
        BtnLog.Content = Loc.S("Update.Button.OpenLog");
        BtnRetry.Content = Loc.S("Update.Button.Retry");
        BtnLaunch.Content = Loc.S("CodexUpdate.Button.LaunchCodex");
        BtnClose.Content = Loc.S("Update.Button.Close");
        RefreshDetail();
        RefreshHint();
        RefreshCountdown();
    }

    private void OnLanguageChanged() => ApplyStrings();
}
