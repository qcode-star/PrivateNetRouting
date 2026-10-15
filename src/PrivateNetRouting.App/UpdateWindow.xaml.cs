using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.App;

/// <summary>
/// Claude Desktop update window: steps "Check versions →
/// Proxy chain → Download → Install", version check before the chain bring-up (without
/// an extra UAC; when no source has a package address, raising the chain is offered once
/// and the check repeats over the live chain), a progress bar with speed/ETA by samples,
/// a pause before installation when
/// Claude is running, deferred installation and "Retry". Installation is not interrupted:
/// "Stop" and closing are unavailable at this step. All core calls are await,
/// the window is not blocked; progress comes via Progress&lt;T&gt; to the UI thread.
/// </summary>
public partial class UpdateWindow : Window
{
    /// <summary>How long the window lives after a successful installation.</summary>
    private const int AutoCloseSeconds = 30;

    private static readonly string[] StepOrder = ["Check", "Chain", "Download", "Install"];

    private readonly ProxyRuntime _runtime;
    private readonly string _logPath;
    private readonly StepRailRow[] _rows;
    private readonly ChainStepStatus[] _states;
    private string? _openedAuthUrl; // the browser opens the login page once per URL
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _autoClose;
    private readonly Stopwatch _watch = new();
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
    private readonly object _sampleGate = new();

    private int _autoCloseLeft;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _pauseDecision;
    private UpdatePhase _phase = UpdatePhase.Check;
    private string? _packageFile;
    private string? _beforeVersion;
    private bool _terminal;
    private long _doneBytes;
    private long _totalBytes;
    private DateTimeOffset _lastProgressAt = DateTimeOffset.Now;
    private bool _stallWarned;
    private string? _detailKey;
    private object?[] _detailArgs = [];
    private string _detailDirect = "";
    private string? _hintKey;
    private object?[] _hintArgs = [];
    private string _hintDirect = "";

    public UpdateWindow(ProxyRuntime runtime)
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowTheme.UseDarkTitleBar(this);
        // the height belongs to the content (scenarios differ: progress bar, manual URL,
        // countdown); the cap keeps a short screen from growing the window off the work area
        MaxHeight = SystemParameters.WorkArea.Height;
        _runtime = runtime;
        _logPath = runtime.Updater.Log.FilePath;

        _rows = new StepRailRow[StepOrder.Length];
        _states = new ChainStepStatus[StepOrder.Length];
        var body = (Style)FindResource("Text.Body");
        var mono = (Style)FindResource("Text.Mono");
        for (var i = 0; i < StepOrder.Length; i++)
        {
            _states[i] = ChainStepStatus.Pending;
            _rows[i] = new StepRailRow(Loc.S($"Update.Step.{StepOrder[i]}"), body, mono);
            _rows[i].Apply(ChainStepStatus.Pending, "");
            StepsPanel.Children.Add(_rows[i].Element);
        }

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _clock.Tick += (_, _) => OnClockTick();

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
                await StartAsync(null);
            }
        };
    }

    /// <summary>
    /// The version state the window has just resolved — by its own check or by a finished
    /// installation. The caller keeps its "update available" line in sync with it: a check that
    /// finds the version current, or an installation that replaced it, leaves that line stale.
    /// </summary>
    public event Action<UpdateCheckResult>? VersionStateResolved;

    /// <summary>Full cycle: check → chain → download → package verification → pause → install.</summary>
    private async Task StartAsync(string? manualUrl)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _terminal = false;
        _stallWarned = false;
        _doneBytes = 0;
        _totalBytes = 0;
        _samples.Clear();
        _beforeVersion = null;
        _watch.Restart();
        _clock.Start();
        ElapsedText.Text = "0:00";
        ShowProgress(true);
        SetProgressUnknown();
        UrlPanel.Visibility = Visibility.Collapsed;
        BtnStop.Visibility = Visibility.Visible;
        BtnRetry.Visibility = Visibility.Collapsed;
        BtnLaunch.Visibility = Visibility.Collapsed;
        BtnInstallNow.Visibility = Visibility.Collapsed;
        BtnDefer.Visibility = Visibility.Collapsed;
        SetHintDirect("");
        for (var i = 0; i < StepOrder.Length; i++)
        {
            SetStep(i, ChainStepStatus.Pending);
        }

        try
        {
            // Version check — before the chain: "nothing to install" brings up nothing and asks no UAC
            SetPhase(UpdatePhase.Check);
            SetDetailKey("Update.Check.Running");

            if (manualUrl is not null && !PackageUrl.IsValidPackageUrl(manualUrl))
            {
                UrlBox.Text = manualUrl;
                UrlPanel.Visibility = Visibility.Visible;
                SetDetailKey("Update.Check.InvalidUrl");
                _clock.Stop();
                UrlBox.Focus();
                return; // waiting for another address from the user
            }

            UpdateCheckResult check;
            var chainRaised = false;
            while (true)
            {
                SetStep(0, ChainStepStatus.Active);
                check = await _runtime.Updater.CheckAsync(new UpdateOptions { ManualUrl = manualUrl }, ct);
                if (check.Installed is null)
                {
                    SetStep(0, ChainStepStatus.Failed);
                    SetDetailKey("Update.Check.NotInstalled");
                    VersionStateResolved?.Invoke(check);
                    Finish(success: false);
                    return;
                }

                _beforeVersion = check.Installed.Version;
                SetStep(0, ChainStepStatus.Done, check.Installed.Version);
                VersionStateResolved?.Invoke(check);

                if (check.UpToDate)
                {
                    for (var i = 1; i < StepOrder.Length; i++)
                    {
                        SetStep(i, ChainStepStatus.Skipped);
                    }

                    SetDetailKey("Update.Check.UpToDateFmt", check.Installed.Version);
                    Finish(success: true);
                    return;
                }

                if (check.PackageUrl is not null)
                {
                    break;
                }

                // No address from any source: the feed answers only through the chain, the journal
                // may be silent. The bring-up is offered once (it can ask for UAC) — the same pass
                // the CLI run makes without asking; a refusal, like a second empty answer, leaves
                // the manual address entry.
                var raise = !chainRaised && MessageBox.Show(
                    this,
                    Loc.S("Update.ConfirmRaiseChain"),
                    Loc.S("Update.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (!raise)
                {
                    UrlPanel.Visibility = Visibility.Visible;
                    SetDetailKey("Update.Check.NoUrl");
                    _clock.Stop();
                    UrlBox.Focus();
                    return; // the address is entered manually
                }

                chainRaised = true;
                SetPhase(UpdatePhase.Chain);
                SetStep(1, ChainStepStatus.Active);
                await _runtime.Updater.EnsureChainAsync(new Progress<ChainStep>(OnChainStep), null, ct);
                SetHintDirect("");
                SetPhase(UpdatePhase.Check);
            }

            SetDetailKey("Update.Check.AvailableFmt", check.Installed.Version, check.OfferedVersion ?? "?");

            // Chain: a live CONNECT closes the step immediately, a dead one — bring-up with substeps (UAC possible)
            SetPhase(UpdatePhase.Chain);
            SetStep(1, ChainStepStatus.Active);
            await _runtime.Updater.EnsureChainAsync(new Progress<ChainStep>(OnChainStep), null, ct);
            SetStep(1, ChainStepStatus.Done);
            SetHintDirect("");

            // Chunked download: the bar is determinate, speed/ETA — by samples over a ~10 s window
            SetPhase(UpdatePhase.Download);
            SetStep(2, ChainStepStatus.Active);
            var updateProgress = new Progress<UpdateProgress>(OnUpdateProgress);
            _packageFile = await _runtime.Updater.DownloadAsync(check.PackageUrl, updateProgress, ct);
            SetStep(2, ChainStepStatus.Done, ByteSize.Format(_totalBytes));
            SetHintDirect("");

            // Package verification: manifest and Publisher
            var identity = await _runtime.Updater.VerifyAsync(_packageFile, ct);
            SetDetailKey("Update.Verify.OkFmt", identity.Version, identity.Publisher);

            // Pause before installation — only if Claude is running (it will be closed)
            if (await Task.Run(() => _runtime.Updater.IsClaudeRunning()))
            {
                ShowProgress(false);
                SetDetailKey("Update.Pause.Message");
                BtnInstallNow.Visibility = Visibility.Visible;
                BtnDefer.Visibility = Visibility.Visible;
                BtnStop.IsEnabled = true;
                _pauseDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var installNow = await _pauseDecision.Task;
                _pauseDecision = null;
                BtnInstallNow.Visibility = Visibility.Collapsed;
                BtnDefer.Visibility = Visibility.Collapsed;
                if (!installNow)
                {
                    SetStep(3, ChainStepStatus.Skipped);
                    SetDetailKey("Update.Done.DeferredFmt", _packageFile);
                    Finish(success: true);
                    return;
                }

                ShowProgress(true);
            }

            // Installation: not interrupted — "Stop"/closing are unavailable
            SetPhase(UpdatePhase.Install);
            SetStep(3, ChainStepStatus.Active);
            SetProgressUnknown();
            var after = await _runtime.Updater.InstallAsync(_packageFile, UpdateOptions.Default, updateProgress, ct);
            SetStep(3, ChainStepStatus.Done, after.Version);
            SetDetailKey("Update.Done.InstalledFmt", _beforeVersion, after.Version);
            if (after.Version == _beforeVersion)
            {
                SetHintKey("Update.Install.VersionWarn");
            }
            else
            {
                // the URL we downloaded from now offers what is installed — no "update available" is left
                VersionStateResolved?.Invoke(new UpdateCheckResult(after, check.PackageUrl, after.Version, UpToDate: true));
            }

            Finish(success: true, showLaunch: true, progressFull: true);
            StartAutoClose();
        }
        catch (OperationCanceledException)
        {
            if (_totalBytes > 0)
            {
                SetDetailKey("Update.Done.StoppedFmt", ByteSize.Format(_doneBytes), ByteSize.Format(_totalBytes));
            }
            else
            {
                SetDetailKey("Update.Done.Stopped");
            }

            Finish(success: false);
        }
        catch (Exception ex)
        {
            MarkCurrentStepFailed();
            SetDetailDirect(ex.Message);
            if (_phase == UpdatePhase.Install && _packageFile is not null)
            {
                SetHintKey("Update.Install.RightsHintFmt", _packageFile);
            }

            Finish(success: false);
        }
    }

    private void OnChainStep(ChainStep step)
    {
        if (_phase != UpdatePhase.Chain)
        {
            return;
        }

        _rows[1].Detail.Text = $"{step.Component}: {step.Detail}";
        if (step.Status == ChainStepStatus.Active && !string.IsNullOrEmpty(step.Detail))
        {
            SetDetailDirect(step.Detail);
        }

        if (step.Component == "Daemon")
        {
            if (step.Status == ChainStepStatus.Active && step.NeedsUac)
            {
                SetHintKey("Update.Hint.Uac");
            }
            else if (step.Status is ChainStepStatus.Done or ChainStepStatus.Skipped)
            {
                SetHintDirect("");
            }
        }

        if (step.Component == "Node")
        {
            if (!string.IsNullOrEmpty(step.AuthUrl) && step.AuthUrl != _openedAuthUrl)
            {
                _openedAuthUrl = step.AuthUrl;
                SetHintKey("Update.Hint.Login");
                try
                {
                    Process.Start(new ProcessStartInfo(step.AuthUrl) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // the URL stays in the step line above — the login is possible without the auto-open
                }
            }
            else if (step.Status is ChainStepStatus.Done or ChainStepStatus.Skipped)
            {
                SetHintDirect("");
            }
        }
    }

    private void OnUpdateProgress(UpdateProgress p)
    {
        if (p.Phase == UpdatePhase.Download && p.TotalBytes > 0)
        {
            _doneBytes = p.DoneBytes;
            _totalBytes = p.TotalBytes;
            _lastProgressAt = DateTimeOffset.Now;
            _stallWarned = false;
            lock (_sampleGate)
            {
                var now = DateTimeOffset.Now;
                _samples.Enqueue((now, p.DoneBytes));
                var cutoff = now - TimeSpan.FromSeconds(12);
                while (_samples.Count > 2 && _samples.Peek().At < cutoff)
                {
                    _samples.Dequeue();
                }
            }

            SetProgress(100.0 * p.DoneBytes / p.TotalBytes);
            UpdateDownloadDetail();
        }
        else if (p.Message.Length > 0)
        {
            SetDetailDirect(p.Message);
        }
    }

    private void UpdateDownloadDetail()
    {
        double rate = 0;
        lock (_sampleGate)
        {
            if (_samples.Count >= 2)
            {
                var oldest = _samples.Peek();
                var newest = _samples.Last();
                var seconds = (newest.At - oldest.At).TotalSeconds;
                if (seconds >= 1)
                {
                    rate = (newest.Bytes - oldest.Bytes) / seconds;
                }
            }
        }

        var eta = rate > 1 && _totalBytes > _doneBytes
            ? TimeSpan.FromSeconds((_totalBytes - _doneBytes) / rate)
            : TimeSpan.Zero;
        SetDetailKey(
            "Update.Download.RunningFmt",
            ByteSize.Format(_doneBytes),
            ByteSize.Format(_totalBytes),
            ByteSize.Format((long)Math.Max(rate, 0)),
            FormatEta(eta));
    }

    private void OnClockTick()
    {
        ElapsedText.Text = $"{(int)_watch.Elapsed.TotalMinutes}:{_watch.Elapsed.Seconds:00}";
        if (!_terminal && _phase == UpdatePhase.Download && _totalBytes > 0)
        {
            UpdateDownloadDetail();
            if (!_stallWarned && DateTimeOffset.Now - _lastProgressAt > TimeSpan.FromMinutes(3))
            {
                _stallWarned = true;
                SetHintKey("Update.Download.StalledFmt", 3);
            }
        }
    }

    private void SetPhase(UpdatePhase phase)
    {
        _phase = phase;
        BtnStop.IsEnabled = phase is UpdatePhase.Chain or UpdatePhase.Download;
        BtnClose.IsEnabled = phase != UpdatePhase.Install;
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
        var index = _phase switch
        {
            UpdatePhase.Check => 0,
            UpdatePhase.Chain => 1,
            UpdatePhase.Download or UpdatePhase.Verify => 2,
            UpdatePhase.Install => 3,
            _ => -1,
        };
        if (index >= 0 && _states[index] is ChainStepStatus.Pending or ChainStepStatus.Active)
        {
            SetStep(index, ChainStepStatus.Failed);
        }
    }

    /// <summary>Terminal state: the clock freezes, "Stop" disappears, "Retry"/"Launch Claude" appear.</summary>
    private void Finish(bool success, bool showLaunch = false, bool progressFull = false)
    {
        _terminal = true;
        _clock.Stop();
        _pauseDecision?.TrySetResult(false);
        _pauseDecision = null;
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
        BtnInstallNow.Visibility = Visibility.Collapsed;
        BtnDefer.Visibility = Visibility.Collapsed;
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
            CountdownText.Text = Loc.F("Update.AutoCloseFmt", _autoCloseLeft);
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_pauseDecision is not null)
        {
            // in pause, stop = defer: the package stays in the cache
            _pauseDecision.TrySetResult(false);
            return;
        }

        _cts?.Cancel();
    }

    private void OnInstallNow(object sender, RoutedEventArgs e) => _pauseDecision?.TrySetResult(true);

    private void OnDefer(object sender, RoutedEventArgs e) => _pauseDecision?.TrySetResult(false);

    private void OnRetry(object sender, RoutedEventArgs e) => _ = StartAsync(null);

    private void OnContinueWithUrl(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        _ = StartAsync(url);
    }

    private async void OnLaunchClaude(object sender, RoutedEventArgs e)
    {
        BtnLaunch.IsEnabled = false;
        try
        {
            // A copy that survived the update without our proxy would swallow the new start
            // (single instance): the same offer as in the main window.
            var running = await Task.Run(() => _runtime.Launcher.InspectRunning());
            if (!LaunchPrompts.ConfirmRestart(this, running, "App.Name.Claude"))
            {
                _runtime.Launcher.NoteRestartDeclined();
                SetHintKey("Update.Launch.Declined");
                return;
            }

            // the launcher itself will check/bring up the chain and pass the proxy flags
            await _runtime.Launcher.LaunchAsync(LaunchPrompts.OptionsFor(running), null, CancellationToken.None);
            SetHintKey("Update.Launch.Done");
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
            if (_phase == UpdatePhase.Install)
            {
                e.Cancel = true;
                SetHintKey("Update.Install.NoClose");
                return;
            }

            if (_phase is UpdatePhase.Chain or UpdatePhase.Download)
            {
                var answer = MessageBox.Show(
                    this,
                    Loc.S("Update.ConfirmClose"),
                    Loc.S("Update.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _pauseDecision?.TrySetResult(false);
            _cts?.Cancel();
        }

        base.OnClosing(e);
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

    private void RefreshDetail() => DetailText.Text = _detailKey is not null ? Loc.F(_detailKey, _detailArgs) : _detailDirect;

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

    private static string FormatEta(TimeSpan eta) => eta <= TimeSpan.Zero
        ? "—"
        : eta.TotalHours >= 1 ? $"~{(int)eta.TotalHours}h{eta.Minutes:00}m" : $"~{(int)eta.TotalMinutes}:{eta.Seconds:00}";

    private void ApplyStrings()
    {
        Title = Loc.S("Update.Title");
        HeaderText.Text = Loc.S("Update.Header");
        SubHeaderText.Text = Loc.S("Update.SubHeader");
        for (var i = 0; i < StepOrder.Length; i++)
        {
            _rows[i].Label.Text = Loc.S($"Update.Step.{StepOrder[i]}");
        }

        BtnStop.Content = Loc.S("Update.Button.Stop");
        BtnLog.Content = Loc.S("Update.Button.OpenLog");
        BtnInstallNow.Content = Loc.S("Update.Button.InstallNow");
        BtnDefer.Content = Loc.S("Update.Button.Defer");
        BtnRetry.Content = Loc.S("Update.Button.Retry");
        BtnLaunch.Content = Loc.S("Update.Button.LaunchClaude");
        BtnClose.Content = Loc.S("Update.Button.Close");
        UrlLabel.Text = Loc.S("Update.Url.Label");
        BtnContinueUrl.Content = Loc.S("Update.Button.ContinueWithUrl");
        RefreshDetail();
        RefreshHint();
        RefreshCountdown();
    }

    private void OnLanguageChanged() => ApplyStrings();
}
