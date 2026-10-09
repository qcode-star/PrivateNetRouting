using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.App;

/// <summary>
/// Main window: chain status and control, UI language selection.
/// The chain is the single lifecycle object of the window: the chain buttons bring it up
/// (idempotently — a partially up chain gets its missing links) and stop it. Chain state is polled
/// less often than the status rows: each poll is a CLI call and a CONNECT roundtrip. All strings
/// come from the Localization/Strings.*.xaml dictionaries: XAML takes them via DynamicResource,
/// code via Loc.S/Loc.F; a language change rebinds the dictionary on the fly (Loc.LanguageChanged),
/// the view model redraws its strings.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The chain stages in bring-up order — the order the rail draws them in.</summary>
    private static readonly string[] ChainComponents = ["Daemon", "Keeper", "Node", "Proxy", "Health"];

    private readonly ProxyRuntime _runtime;
    private readonly MainViewModel _viewModel;
    private readonly ChainRail _rail;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chainTimer;
    private readonly DispatcherTimer _guardTimer;
    private readonly DispatcherTimer _busyClock;
    private readonly Stopwatch _busyWatch = new();
    private ChainState? _lastChainState;
    private GuardSnapshot? _lastGuards;
    private bool _guardBusy;
    private bool _chainBusy;
    private bool _launchBusy;
    private bool _fillingLanguages;
    private UpdateWindow? _updateWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private bool _idleStopBusy;
    private bool _autoStartSyncing;
    private bool _chainCardExpanded;
    private bool _guardsCardExpanded;
    private TrayIcon? _tray;

    /// <summary>The collapsed card was expanded by a chain operation, not by the user — it goes back on success.</summary>
    private bool _chainCardAutoExpanded;

    public MainWindow(ProxyRuntime runtime, string configPath, string languagePreference)
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        WindowTheme.UseDarkTitleBar(this);
        MaxHeight = SystemParameters.WorkArea.Height; // past the cap the body scrolls instead of growing
        _runtime = runtime;
        _viewModel = new MainViewModel(runtime) { ConfigPath = configPath };
        DataContext = _viewModel;
        _rail = new ChainRail(RailHost);
        _rail.Reset(RailStage.Unknown);
        _viewModel.Refresh();
        SyncAutoStartBox();
        _chainCardExpanded = _runtime.Config.Ui.ChainCardExpanded;
        _guardsCardExpanded = _runtime.Config.Ui.GuardsCardExpanded;
        ApplyCardLayout();
        FillLanguages(languagePreference);

        _busyClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _busyClock.Tick += (_, _) => BusyElapsed.Text = FormatElapsed(_busyWatch.Elapsed);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();
        _refreshTimer.Start();

        _chainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _chainTimer.Tick += async (_, _) =>
        {
            await RefreshChainAsync();
            await RefreshIdleStopAsync();
        };
        _chainTimer.Start();
        _ = RefreshChainAsync();
        _ = RefreshIdleStopAsync();

        // guard statuses — read-only (registry/hosts/NRPT/firewall/WSL), but NRPT and firewall
        // are read by PowerShell cmdlets — polled less often than chain state
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _guardTimer.Tick += async (_, _) => await RefreshGuardsAsync();
        _guardTimer.Start();
        Loc.LanguageChanged += OnLanguageChanged;
        _ = RefreshGuardsAsync();
    }

    /// <summary>Minimizing hides to the tray (the app keeps running) — same as closing the window.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _chainTimer.Stop();
        _guardTimer.Stop();
        _busyClock.Stop();
        if (_tray is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _tray = null;
        }

        Loc.LanguageChanged -= OnLanguageChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// The tray's action items mirror the card's buttons: this window owns the operations and
    /// pushes its Can* flags into the tray on every change; the tray's chain and launch requests
    /// come back as the public ChainUpAsync/ChainDownAsync/LaunchClaudeAsync.
    /// </summary>
    internal void AttachTray(TrayIcon tray)
    {
        _tray = tray;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        PushTrayActions();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CanChainUp)
            or nameof(MainViewModel.CanChainDown)
            or nameof(MainViewModel.CanLaunch))
        {
            PushTrayActions();
        }
    }

    private void PushTrayActions() =>
        _tray?.UpdateActions(_viewModel.CanChainUp, _viewModel.CanChainDown, _viewModel.CanLaunch);

    /// <summary>A language change redraws everything the window has already painted by hand.</summary>
    private void OnLanguageChanged()
    {
        RenderGuards();
        RenderGuardSummary();
        UpdateGuardAllButton();
        ApplyCardLayout(); // the toggle tooltips and the guard-all word follow the language
        _rail.Relabel();
        if (_lastChainState is not null)
        {
            RenderRail(_lastChainState);
        }
    }

    // ------------------------------------------------------------------ card collapse

    /// <summary>
    /// Applies the collapsed/expanded state of both cards: the chevron hides its card's detail
    /// (rail and status rows for the chain, note and layers for the guards) and leaves the
    /// compact row — title, overall status dot, the main control. The choice comes from the
    /// user config and goes back into it only on an explicit toggle.
    /// </summary>
    private void ApplyCardLayout()
    {
        ChainDetails.Visibility = _chainCardExpanded ? Visibility.Visible : Visibility.Collapsed;
        ChainDot.Visibility = _chainCardExpanded ? Visibility.Collapsed : Visibility.Visible;
        ApplyToggleChrome(ChainCardToggle, ChainCardChevron, _chainCardExpanded);

        GuardDetails.Visibility = _guardsCardExpanded ? Visibility.Visible : Visibility.Collapsed;
        GuardDot.Visibility = _guardsCardExpanded ? Visibility.Collapsed : Visibility.Visible;
        GuardSummary.Visibility = _guardsCardExpanded ? Visibility.Collapsed : Visibility.Visible;
        GuardAllButton.Visibility = _guardsCardExpanded ? Visibility.Collapsed : Visibility.Visible;
        GuardRefreshButton.Visibility = _guardsCardExpanded ? Visibility.Visible : Visibility.Collapsed;
        ApplyToggleChrome(GuardCardToggle, GuardCardChevron, _guardsCardExpanded);
    }

    /// <summary>The chevron points where the card goes; the word sits in the tooltip and the name.</summary>
    private void ApplyToggleChrome(System.Windows.Controls.Button toggle, System.Windows.Shapes.Path chevron, bool expanded)
    {
        chevron.Data = (Geometry)FindResource(expanded ? "Mark.Chevron.Up" : "Mark.Chevron.Down");
        var label = Loc.S(expanded ? "Main.Card.Collapse" : "Main.Card.Expand");
        toggle.ToolTip = label;
        AutomationProperties.SetName(toggle, label);
    }

    private void OnToggleChainCard(object sender, RoutedEventArgs e) => SetChainCardExpanded(!_chainCardExpanded, persist: true);

    private void OnToggleGuardsCard(object sender, RoutedEventArgs e)
    {
        _guardsCardExpanded = !_guardsCardExpanded;
        ApplyCardLayout();
        try
        {
            AppConfigStore.Update(config => config.Ui.GuardsCardExpanded = _guardsCardExpanded);
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
    }

    private void SetChainCardExpanded(bool expanded, bool persist)
    {
        _chainCardExpanded = expanded;
        _chainCardAutoExpanded = false; // a manual toggle owns the state from here on
        ApplyCardLayout();
        if (!persist)
        {
            return;
        }

        try
        {
            AppConfigStore.Update(config => config.Ui.ChainCardExpanded = expanded);
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
    }

    /// <summary>
    /// A chain operation needs the rail visible: a collapsed card expands for it (without
    /// touching the stored choice) and, on success, collapses back; on failure it stays
    /// expanded so the failing stage remains on screen.
    /// </summary>
    private void ExpandChainCardForOperation()
    {
        if (_chainCardExpanded)
        {
            return;
        }

        _chainCardExpanded = true;
        _chainCardAutoExpanded = true;
        ApplyCardLayout();
    }

    private void SettleChainCardAfterOperation(bool success)
    {
        if (!_chainCardAutoExpanded)
        {
            return;
        }

        _chainCardAutoExpanded = false;
        if (success)
        {
            SetChainCardExpanded(false, persist: false);
        }
    }

    private async Task RefreshChainAsync()
    {
        if (_chainBusy || _launchBusy)
        {
            return; // an operation in flight owns the rail — a poll must not overwrite its progress
        }

        try
        {
            var state = await _runtime.Chain.GetStateAsync(CancellationToken.None);
            _lastChainState = state;
            _viewModel.UpdateChain(state);
            RenderRail(state);
        }
        catch (Exception ex)
        {
            _viewModel.SetChainStateError(ex.Message);
        }
    }

    /// <summary>
    /// The chain rail from a snapshot. The colours stay calm on purpose: a chain that is down is the
    /// normal resting state of this application, so it is neutral — amber means "present but not
    /// usable", red is reserved for a real fault.
    /// </summary>
    private void RenderRail(ChainState state)
    {
        _rail.SetStage(0, state.DaemonProcessAlive
            ? new RailStage(
                state.DaemonHttpListening ? Loc.S("Main.Chain.Daemon.Listening") : Loc.S("Main.Chain.Daemon.NoListener"),
                state.DaemonHttpListening ? Theme.Ok : Theme.Warn)
            : new RailStage(Loc.S("Main.Chain.Daemon.Down"), Theme.Idle));

        _rail.SetStage(1, new RailStage(
            state.KeeperRunning ? Loc.S("Main.Chain.Keeper.Running") : Loc.S("Main.Chain.Keeper.Down"),
            state.KeeperRunning ? Theme.Ok : Theme.Idle));

        _rail.SetStage(2, state.BackendState is null
            ? new RailStage(Loc.S("Main.Chain.Node.NoResponse"), Theme.Idle)
            : new RailStage(state.BackendState, state.BackendState == "Running" ? Theme.Ok : Theme.Warn));

        _rail.SetStage(3, new RailStage(
            state.ProxyRunning ? Loc.S("Main.Chain.Proxy.Running") : Loc.S("Main.Chain.Proxy.Stopped"),
            state.ProxyRunning ? Theme.Ok : Theme.Idle));

        _rail.SetStage(4, !state.ProxyRunning
            ? new RailStage(Loc.S("Main.Chain.Health.Idle"), Theme.Idle)
            : new RailStage(
                state.ProxyHealthy ? Loc.S("Main.Chain.Health.Ok") : Loc.S("Main.Chain.Health.Fail"),
                state.ProxyHealthy ? Theme.Ok : Theme.Bad));
    }

    /// <summary>
    /// A bring-up step from the orchestrator: the rail runs live instead of waiting for the next
    /// poll, the current step's detail sits under it, and the UAC hint appears with the daemon
    /// only when the start actually goes through elevation (chain.daemonElevation).
    /// </summary>
    private void OnChainStep(ChainStep step)
    {
        var stage = step.Status switch
        {
            ChainStepStatus.Active => new RailStage(Loc.S("Main.Rail.Starting"), Theme.Accent),
            ChainStepStatus.Done => new RailStage(Loc.S("Main.Rail.Ready"), Theme.Ok, RailMark.Check),
            ChainStepStatus.Skipped => new RailStage(Loc.S("Main.Chain.Skipped"), Theme.Ok, RailMark.Check),
            ChainStepStatus.Failed => new RailStage(Loc.S("Main.Rail.Failed"), Theme.Bad, RailMark.Cross),
            _ => new RailStage(Loc.S("Main.Rail.Waiting"), Theme.Idle),
        };
        _rail.SetStage(Array.IndexOf(ChainComponents, step.Component), stage);

        if (step.Component == "Daemon")
        {
            if (step.Status == ChainStepStatus.Active && step.NeedsUac)
            {
                ChainHint.Text = Loc.S("Main.Chain.Hint.Uac");
                ChainHint.Visibility = Visibility.Visible;
            }
            else if (step.Status is ChainStepStatus.Done or ChainStepStatus.Skipped)
            {
                ChainHint.Visibility = Visibility.Collapsed;
            }
        }

        if (!string.IsNullOrEmpty(step.Detail))
        {
            ChainDetail.Text = step.Detail;
        }
    }

    /// <summary>The card shows a live operation: spinner, elapsed time and a line for the steps.</summary>
    private void BeginOperation()
    {
        ExpandChainCardForOperation();
        _busyWatch.Restart();
        BusyElapsed.Text = FormatElapsed(TimeSpan.Zero);
        BusySpinner.Visibility = Visibility.Visible;
        BusyElapsed.Visibility = Visibility.Visible;
        ChainDetail.Text = "";
        ChainDetail.Visibility = Visibility.Visible;
        ChainHint.Visibility = Visibility.Collapsed;
        _rail.Reset(new RailStage(Loc.S("Main.Rail.Waiting"), Theme.Idle));
        SpinnerArc.RenderTransform.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.05)) { RepeatBehavior = RepeatBehavior.Forever });
        _busyClock.Start();
    }

    private void EndOperation()
    {
        _busyClock.Stop();
        BusySpinner.Visibility = Visibility.Collapsed;
        BusyElapsed.Visibility = Visibility.Collapsed;
        ChainDetail.Visibility = Visibility.Collapsed;
        ChainHint.Visibility = Visibility.Collapsed;
        SpinnerArc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private static string FormatElapsed(TimeSpan elapsed) => $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

    /// <summary>
    /// Direct-access guards: statuses are read in full (registry, hosts, NRPT/firewall,
    /// settings.json, WSL) and color the card; "Refresh" and the timer — the same path.
    /// </summary>
    private async Task RefreshGuardsAsync()
    {
        if (_guardBusy)
        {
            return;
        }

        try
        {
            _lastGuards = await _runtime.Guard.GetStatusAsync(CancellationToken.None);
            RenderGuards();
        }
        catch (Exception ex)
        {
            _lastGuards = null;
            BanDetail.Text = PolicyDetail.Text = SettingsDetail.Text = WslDetail.Text = ex.Message;
            SetGuardDots(GuardState.Unknown, GuardState.Unknown, GuardState.Unknown, GuardState.Unknown);
            RenderGuardSummary();
            UpdateGuardAllButton();
        }
    }

    private void RenderGuards()
    {
        var snapshot = _lastGuards;
        if (snapshot is null)
        {
            return;
        }

        SetGuardDots(snapshot.Ban.State, snapshot.Policy.State, snapshot.Settings.State, snapshot.Wsl.State);

        BanDetail.Text = snapshot.Ban.Detail ?? (snapshot.Ban.State switch
        {
            GuardState.Off => Loc.S("Main.Guard.Ban.Off"),
            GuardState.Unknown => Loc.S("Main.Guard.Unknown"),
            _ => Loc.F("Main.Guard.Ban.DetailFmt", snapshot.Ban.HostsEntries, snapshot.Ban.NrptNamespaces.Count, snapshot.Ban.FirewallRules),
        });
        BanDetail.ToolTip = snapshot.Ban.HostsEntries > 0 && !snapshot.Ban.HostsCurrent
            ? Loc.S("Main.Guard.Ban.Stale")
            : null;

        PolicyDetail.Text = snapshot.Policy.State switch
        {
            GuardState.On => Loc.F("Main.Guard.Policy.DetailFmt", snapshot.Policy.CurrentUrl),
            GuardState.Off => Loc.S("Main.Guard.Policy.Off"),
            GuardState.Partial => Loc.F("Main.Guard.Policy.OtherFmt", snapshot.Policy.CurrentUrl, snapshot.Policy.ExpectedUrl),
            _ => snapshot.Policy.Detail ?? Loc.S("Main.Guard.Unknown"),
        };

        SettingsDetail.Text = snapshot.Settings.State switch
        {
            GuardState.On => Loc.F("Main.Guard.Settings.OkFmt", snapshot.Settings.ExpectedProxy),
            GuardState.Off => Loc.S("Main.Guard.Settings.NoFile"),
            GuardState.Partial => Loc.F(
                "Main.Guard.Settings.MismatchFmt",
                snapshot.Settings.CurrentProxy ?? "—",
                snapshot.Settings.CurrentNoProxy ?? "—"),
            _ => snapshot.Settings.Detail ?? Loc.S("Main.Guard.Unknown"),
        };

        WslDetail.Text = snapshot.Wsl.Distros.Count == 0
            ? LocalizeWslReason(snapshot.Wsl.Reason)
            : string.Join("; ", snapshot.Wsl.Distros.Select(LocalizeDistroStatus));
        WslDetail.ToolTip = snapshot.Wsl.Distros.Count == 0
            ? null
            : string.Join("\n", snapshot.Wsl.Distros.Select(d => $"{d.Distro}: {d.Reason}"));

        RenderGuardSummary();
        UpdateGuardAllButton();
    }

    /// <summary>
    /// The collapsed card's one status line: the four layer states folded into one. Unknown
    /// dominates (nobody knows), any mix folds to partial; only all-on is good, all-off is the
    /// only bad one.
    /// </summary>
    private void RenderGuardSummary()
    {
        var state = _lastGuards is null
            ? GuardState.Unknown
            : Merge(Merge(_lastGuards.Ban.State, _lastGuards.Policy.State), Merge(_lastGuards.Settings.State, _lastGuards.Wsl.State));

        var (word, brush) = state switch
        {
            GuardState.On => (Loc.S("Main.Guard.Summary.AllOn"), Theme.Ok),
            GuardState.Off => (Loc.S("Main.Guard.Summary.AllOff"), Theme.Bad),
            GuardState.Partial => (Loc.S("Main.Guard.Summary.Mixed"), Theme.Warn),
            _ => (Loc.S("Main.Guard.Unknown"), Theme.TextMuted),
        };
        GuardDot.Fill = brush;
        GuardSummary.Text = word;

        // the same readability rule as the rows: an unknown state keeps the muted text colour
        GuardSummary.Foreground = state == GuardState.Unknown ? Theme.TextMuted : brush;

        static GuardState Merge(GuardState a, GuardState b)
        {
            if (a == b)
            {
                return a;
            }

            return a == GuardState.Unknown || b == GuardState.Unknown ? GuardState.Unknown : GuardState.Partial;
        }
    }

    /// <summary>
    /// The collapsed card's single action follows the state: everything on — "Disable all"
    /// (ban and policy; the sync layers have no "off"), anything less — "Enable all".
    /// </summary>
    private void UpdateGuardAllButton()
    {
        var allOn = _lastGuards is { } snapshot
            && snapshot.Ban.State == GuardState.On
            && snapshot.Policy.State == GuardState.On
            && snapshot.Settings.State == GuardState.On
            && snapshot.Wsl.State == GuardState.On;
        GuardAllButton.Content = Loc.S(allOn ? "Main.Guard.Button.DisableAll" : "Main.Guard.Button.EnableAll");
    }

    /// <summary>
    /// The combined guard action of the collapsed card, one UAC prompt: ban and policy go as a
    /// single elevated command; when enabling, the no-elevation syncs (settings.json, WSL)
    /// follow in-process.
    /// </summary>
    private async void OnGuardAll(object sender, RoutedEventArgs e)
    {
        var disable = _lastGuards is { } snapshot
            && snapshot.Ban.State == GuardState.On
            && snapshot.Policy.State == GuardState.On
            && snapshot.Settings.State == GuardState.On
            && snapshot.Wsl.State == GuardState.On;
        await RunGuardAsync(async () =>
        {
            var all = await _runtime.Guard.AllAsync(!disable, CancellationToken.None);
            var dns = Loc.S(all.Live.DnsBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open");
            var direct = Loc.S(all.Live.DirectBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open");
            if (disable)
            {
                _viewModel.SetCheckResult("Main.Guard.All.DisabledFmt", dns, direct);
                return;
            }

            await _runtime.Guard.SyncSettingsAsync(CancellationToken.None);
            var wsl = await _runtime.Guard.SyncWslAsync(CancellationToken.None);
            if (wsl.LayerDisabled)
            {
                _viewModel.SetCheckResult("Main.Guard.All.EnabledNoWslFmt", dns, direct, LocalizeWslReason(wsl.Reason ?? ""));
            }
            else
            {
                _viewModel.SetCheckResult("Main.Guard.All.EnabledFmt", dns, direct);
            }
        });
    }

    /// <summary>Line of a single distro: name + localized state (details are in the tooltip).</summary>
    private static string LocalizeDistroStatus(WslDistroStatus distro) => distro.State switch
    {
        GuardState.On => Loc.F("Main.Guard.Wsl.DistroOnFmt", distro.Distro, distro.ScriptPort),
        GuardState.Off => $"{distro.Distro}: {Loc.S("Main.Guard.State.Off")}",
        GuardState.Partial => $"{distro.Distro}: {Loc.S("Main.Guard.State.Partial")}",
        _ => $"{distro.Distro}: {LocalizeWslReason(distro.Reason)}",
    };

    private static string LocalizeWslReason(string reason)
    {
        if (reason.Contains("mode=off", StringComparison.Ordinal))
        {
            return Loc.S("Main.Guard.Wsl.Off.Mode");
        }

        if (reason.Contains("no distros configured", StringComparison.Ordinal))
        {
            return Loc.S("Main.Guard.Wsl.NotConfigured");
        }

        if (reason.Contains("auto", StringComparison.Ordinal))
        {
            return Loc.S("Main.Guard.Wsl.AutoNone");
        }

        if (reason.Contains("not running", StringComparison.Ordinal))
        {
            return Loc.S("Main.Guard.Wsl.NotRunning");
        }

        if (reason.Contains("not installed", StringComparison.Ordinal))
        {
            return Loc.S("Main.Guard.Wsl.NotInstalled");
        }

        if (reason.Contains("startup script not found", StringComparison.Ordinal))
        {
            return Loc.S("Main.Guard.Wsl.NoScript");
        }

        return reason;
    }

    /// <summary>Marker, state word and detail of the four guard layers; the word repeats what the colour shows.</summary>
    private void SetGuardDots(GuardState ban, GuardState policy, GuardState settings, GuardState wsl)
    {
        RenderGuardRow(BanDot, BanState, ban);
        RenderGuardRow(PolicyDot, PolicyState, policy);
        RenderGuardRow(SettingsDot, SettingsState, settings);
        RenderGuardRow(WslDot, WslState, wsl);

        static void RenderGuardRow(System.Windows.Shapes.Ellipse dot, TextBlock stateText, GuardState state)
        {
            var brush = Theme.GuardBrush(state);
            dot.Fill = brush;
            stateText.Text = Theme.GuardWord(state);

            // the neutral grey of the marker is below AA as text, so an unknown state stays readable
            stateText.Foreground = state == GuardState.Unknown ? Theme.TextMuted : brush;
        }
    }

    /// <summary>Ban and policy go through the elevated helper (UAC); live checks — right after.</summary>
    private async void OnGuardBanOn(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.BanAsync(enable: true, CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Ban.LiveFmt",
            Loc.S(result.Live.DnsBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"),
            Loc.S(result.Live.DirectBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"));
    });

    private async void OnGuardBanOff(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.BanAsync(enable: false, CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Ban.LiveFmt",
            Loc.S(result.Live.DnsBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"),
            Loc.S(result.Live.DirectBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"));
    });

    private async void OnGuardPolicyOn(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        await _runtime.Guard.PolicyAsync(enable: true, CancellationToken.None);
        _viewModel.SetCheckResult("Main.Guard.Policy.OnDone");
    });

    private async void OnGuardPolicyOff(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        await _runtime.Guard.PolicyAsync(enable: false, CancellationToken.None);
        _viewModel.SetCheckResult("Main.Guard.Policy.OffDone");
    });

    /// <summary>Writing env (proxy + NO_PROXY) into Claude Code settings.json; a backup next to the file.</summary>
    private async void OnGuardSettingsSync(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.SyncSettingsAsync(CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Settings.DoneFmt",
            result.ProxyUrl,
            result.BackupPath ?? Loc.S("Main.Guard.Backup.None"));
    });

    /// <summary>Updating the WSL startup script and distro settings.json (does not start them).</summary>
    private async void OnGuardWslSync(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.SyncWslAsync(CancellationToken.None);
        if (result.LayerDisabled)
        {
            _viewModel.SetCheckResult("Main.Guard.Wsl.SkippedFmt", LocalizeWslReason(result.Reason ?? ""));
            return;
        }

        var lines = result.Distros.Select(d => d.Skipped
            ? Loc.F("Main.Guard.Wsl.DistroSkippedFmt", d.Distro, LocalizeWslReason(d.Reason ?? ""))
            : Loc.F("Main.Guard.Wsl.DistroDoneFmt", d.Distro, d.ProxyUrl ?? ""));
        _viewModel.SetCheckText(string.Join("; ", lines));
    });

    private async void OnRefreshGuards(object sender, RoutedEventArgs e) => await RefreshGuardsAsync();

    private async Task RunGuardAsync(Func<Task> action)
    {
        if (_guardBusy)
        {
            return;
        }

        _guardBusy = true;
        _viewModel.SetGuardBusy(true);
        _viewModel.SetCheckResult("Main.Guard.Busy");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _guardBusy = false;
            _viewModel.SetGuardBusy(false);
            await RefreshGuardsAsync();
        }
    }

    private async void OnCheckChain(object sender, RoutedEventArgs e)
    {
        _viewModel.SetCheckResult("Main.Check.Running");
        var alive = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15));
        _viewModel.SetCheckResult(
            alive ? "Main.Check.AliveFmt" : "Main.Check.DeadFmt",
            _runtime.Config.ForwardProxy.Listen);
    }

    private async void OnChainUp(object sender, RoutedEventArgs e) => await ChainUpAsync();

    private async void OnChainDown(object sender, RoutedEventArgs e) => await ChainDownAsync();

    /// <summary>
    /// Chain bring-up, shared by the card button and the tray item: the card draws the steps
    /// (the window may be hidden), the tray enablement follows the same Can* flags.
    /// </summary>
    public async Task ChainUpAsync()
    {
        if (_chainBusy || _launchBusy)
        {
            return;
        }

        if (!await RunChainBusy(() => _runtime.Chain.EnsureRunningAsync(new Progress<ChainStep>(OnChainStep), CancellationToken.None)))
        {
            return;
        }

        _viewModel.SetCheckResult("Main.Chain.UpDone");
        await RefreshChainAsync();
    }

    /// <summary>Chain shutdown, shared by the card button and the tray item.</summary>
    public async Task ChainDownAsync()
    {
        if (_chainBusy || _launchBusy)
        {
            return;
        }

        if (!await RunChainBusy(() => _runtime.Chain.StopAsync(new Progress<ChainStep>(OnChainStep), CancellationToken.None)))
        {
            return;
        }

        _viewModel.SetCheckResult("Main.Chain.DownDone");
        await RefreshChainAsync();
    }

    private async Task<bool> RunChainBusy(Func<Task> action)
    {
        if (_chainBusy)
        {
            return false;
        }

        _chainBusy = true;
        _viewModel.SetChainBusy(true);
        BeginOperation();
        var success = false;
        try
        {
            await action();
            success = true;
            return true;
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
            return false;
        }
        finally
        {
            _chainBusy = false;
            _viewModel.SetChainBusy(false);
            EndOperation();
            SettleChainCardAfterOperation(success);
        }
    }

    /// <summary>
    /// Idle auto-stop toggle: Start spawns the detached worker (it outlives the application and
    /// switches itself off after the idle stop — rule #4: the background process exists only
    /// after this explicit action), Stop kills the identified stoppers. The row status is read
    /// back from the process list, not from our intention.
    /// </summary>
    private async void OnIdleStopToggled(object sender, RoutedEventArgs e)
    {
        if (_idleStopBusy)
        {
            return;
        }

        _idleStopBusy = true;
        _viewModel.SetIdleStopBusy(true);
        try
        {
            if (_viewModel.IdleStopRunning)
            {
                await _runtime.WatchdogControl.StopAsync(CancellationToken.None);
                _viewModel.SetCheckResult("Main.IdleStop.StoppedManual");
            }
            else
            {
                await _runtime.WatchdogControl.StartAsync(CancellationToken.None);
                _viewModel.SetCheckResult("Main.IdleStop.StartedFmt", _runtime.Config.Watchdog.CheckSeconds);
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _idleStopBusy = false;
            _viewModel.SetIdleStopBusy(false);
            await RefreshIdleStopAsync();
        }
    }

    /// <summary>Liveness of the idle stopper: a process list search by the command line (worker marker / CLI form).</summary>
    private async Task RefreshIdleStopAsync()
    {
        if (_idleStopBusy)
        {
            return;
        }

        try
        {
            var running = await Task.Run(() => _runtime.WatchdogControl.IsRunning(), CancellationToken.None);
            _viewModel.UpdateIdleStop(running);
        }
        catch (Exception)
        {
            _viewModel.SetIdleStopUnknown(); // a failed search is not worth the result line, the next poll retries
        }
    }

    /// <summary>
    /// The auto-start flag (watchdog.autoStart): when on, the chain orchestrator re-arms the
    /// stopper itself after every successful raise. The choice is persisted to the user config
    /// and applied to the shared config object, so the next raise sees it immediately.
    /// </summary>
    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_autoStartSyncing)
        {
            return;
        }

        var enabled = AutoStartBox.IsChecked == true;
        try
        {
            AppConfigStore.Update(config => config.Watchdog.AutoStart = enabled);
            _runtime.Config.Watchdog.AutoStart = enabled;
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
    }

    /// <summary>The checkbox follows the config: on window construction and after a config reload.</summary>
    private void SyncAutoStartBox()
    {
        _autoStartSyncing = true;
        AutoStartBox.IsChecked = _runtime.Config.Watchdog.AutoStart;
        _autoStartSyncing = false;
    }

    /// <summary>Opens the config folder in Explorer with the file selected — the user edits it in their own editor.</summary>
    private void OnConfigOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_viewModel.ConfigPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
    }

    /// <summary>
    /// Re-reads the edited config: the sections are swapped in the shared object, so the
    /// launchers, guards, updater and the next idle stopper start see the fresh values; the
    /// UI language re-applies immediately. Listener values apply after the application restart.
    /// </summary>
    private void OnConfigReload(object sender, RoutedEventArgs e)
    {
        try
        {
            _runtime.ReloadConfig();
            SyncAutoStartBox();
            _chainCardExpanded = _runtime.Config.Ui.ChainCardExpanded;
            _guardsCardExpanded = _runtime.Config.Ui.GuardsCardExpanded;
            ApplyCardLayout();
            var preference = _runtime.Config.Ui.Language;
            Loc.Apply(AppLanguage.Resolve(preference, CultureInfo.CurrentUICulture));
            FillLanguages(preference);
            _viewModel.SetCheckResult("Main.Config.Reloaded");
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
    }

    /// <summary>
    /// Launching Claude: first a live CONNECT roundtrip; if the chain
    /// responds — Claude starts immediately, without any wait. Otherwise the chain card takes over:
    /// the rail runs the bring-up steps live, with the spinner, the elapsed time and — in the
    /// elevated mode — the UAC hint, and a failure is reported in the result line.
    /// </summary>
    private async void OnLaunchClaude(object sender, RoutedEventArgs e) => await RunLaunchAsync();

    /// <summary>
    /// Shortcut mode (App --launch instead of the Claude shortcut): Claude is already running — just
    /// show the window, without a second instance; not running — a normal launch whose bring-up is
    /// drawn in the chain card (tunnel start, checks, then Claude with proxy flags).
    /// </summary>
    public async void LaunchFromShortcut()
    {
        if (_launchBusy)
        {
            return;
        }

        bool claudeRunning;
        try
        {
            claudeRunning = _runtime.Updater.IsClaudeRunning();
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
            return;
        }

        if (claudeRunning)
        {
            _viewModel.SetCheckResult("Main.Launch.AlreadyRunning");
            return;
        }

        await RunLaunchAsync();
    }

    /// <summary>Launching Claude from the tray: the same path as the window's launch button.</summary>
    public async Task LaunchClaudeAsync()
    {
        if (_chainBusy || _launchBusy)
        {
            return;
        }

        await RunLaunchAsync();
    }

    private async Task RunLaunchAsync()
    {
        if (_launchBusy)
        {
            return;
        }

        _launchBusy = true;
        _viewModel.SetLaunchBusy(true);
        var success = false;
        try
        {
            var healthy = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15));
            if (!healthy)
            {
                BeginOperation();
            }

            var progress = healthy ? null : new Progress<ChainStep>(OnChainStep);
            var result = await _runtime.Launcher.LaunchAsync(LauncherOptions.Default, progress, CancellationToken.None);
            _viewModel.SetCheckResult("Main.Launch.DoneFmt", Math.Round(result.Elapsed.TotalSeconds, 1));
            success = true;
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _launchBusy = false;
            _viewModel.SetLaunchBusy(false);
            EndOperation();
            SettleChainCardAfterOperation(success);
            await RefreshChainAsync();
        }
    }

    /// <summary>
    /// Browser launch: the chain is checked with a live CONNECT roundtrip and brought up
    /// idempotently if it is down; the bring-up runs in the chain card, in the main window.
    /// An already running instance of ours is not duplicated (Firefox on the same profile hits a
    /// lock) — a note in the result line.
    /// </summary>
    private async void OnLaunchBrowser(object sender, RoutedEventArgs e) => await RunBrowserLaunchAsync();

    /// <summary>
    /// Shortcut mode (App --launch-browser instead of the "Claude (browser)" shortcut): a browser
    /// with our profile is already running — just the application window; not running — a normal launch.
    /// </summary>
    public async void LaunchBrowserFromShortcut() => await RunBrowserLaunchAsync();

    private async Task RunBrowserLaunchAsync()
    {
        if (_launchBusy)
        {
            return;
        }

        bool browserRunning;
        try
        {
            browserRunning = _runtime.Browser.IsOurBrowserRunning();
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
            return;
        }

        if (browserRunning)
        {
            _viewModel.SetCheckResult("Main.Browser.AlreadyRunning");
            return;
        }

        _launchBusy = true;
        _viewModel.SetLaunchBusy(true);
        var success = false;
        try
        {
            var healthy = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15));
            if (!healthy)
            {
                BeginOperation();
            }

            var progress = healthy ? null : new Progress<ChainStep>(OnChainStep);
            var result = await _runtime.Browser.LaunchAsync(BrowserLauncherOptions.Default, progress, CancellationToken.None);
            _viewModel.SetCheckResult("Main.Browser.DoneFmt", Math.Round(result.Elapsed.TotalSeconds, 1));
            success = true;
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _launchBusy = false;
            _viewModel.SetLaunchBusy(false);
            EndOperation();
            SettleChainCardAfterOperation(success);
            await RefreshChainAsync();
        }
    }

    /// <summary>
    /// Signal about a Claude update found at startup: a line next to the launch buttons.
    /// Returns the localized text — App uses it for the tray balloon as well.
    /// </summary>
    public string SetUpdateNotice(string installedVersion, string? offeredVersion)
    {
        _viewModel.SetUpdateNotice(installedVersion, offeredVersion);
        return offeredVersion is null
            ? Loc.S("Main.Update.Available")
            : Loc.F("Main.Update.AvailableFmt", installedVersion, offeredVersion);
    }

    /// <summary>
    /// Claude Desktop update window (MSIX via the proxy chain): one per application,
    /// a repeated click activates the open one.
    /// </summary>
    private void OnUpdateClaude(object sender, RoutedEventArgs e)
    {
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(_runtime) { Owner = this };
        _updateWindow.VersionStateResolved += OnVersionStateResolved;
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
    }

    /// <summary>
    /// The update window has resolved the version state (its own check, or a finished
    /// installation): the line about an available update follows it — a stale one is dropped,
    /// a still-valid one is reused.
    /// </summary>
    private void OnVersionStateResolved(UpdateCheckResult check)
    {
        if (check.Installed is null || check.UpToDate)
        {
            _viewModel.ClearUpdateNotice();
            return;
        }

        if (check.OfferedVersion is not null)
        {
            SetUpdateNotice(check.Installed.Version, check.OfferedVersion);
        }
    }

    /// <summary>
    /// Diagnostics window, read-only: state summary and log
    /// viewing; one per application, a repeated click activates the open one.
    /// </summary>
    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is not null)
        {
            _diagnosticsWindow.Activate();
            return;
        }

        _diagnosticsWindow = new DiagnosticsWindow(_runtime) { Owner = this };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    /// <summary>Fills the language selector: auto + supported; selection — by the config value.</summary>
    private void FillLanguages(string preference)
    {
        _fillingLanguages = true;
        var wanted = preference?.Trim().ToLowerInvariant();
        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(new ComboBoxItem { Tag = AppLanguage.Auto, Content = Loc.S("Main.Language.Auto") });
        foreach (var code in AppLanguage.Supported)
        {
            LanguageBox.Items.Add(new ComboBoxItem { Tag = code, Content = LanguageDisplayName(code) });
        }

        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == wanted)
            ?? LanguageBox.Items[0];
        _fillingLanguages = false;
    }

    private static string LanguageDisplayName(string code)
    {
        var culture = CultureInfo.GetCultureInfo(code);
        return culture.TextInfo.ToTitleCase(culture.NativeName);
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingLanguages || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string preference })
        {
            return;
        }

        try
        {
            Loc.Apply(AppLanguage.Resolve(preference, CultureInfo.CurrentUICulture));
            AppConfigStore.Update(config => config.Ui.Language = preference);
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
            return;
        }

        FillLanguages(preference);
        _viewModel.SetCheckResult(
            "Main.LanguageSavedFmt",
            preference == AppLanguage.Auto ? Loc.S("Main.Language.Auto") : preference);
    }
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProxyRuntime _runtime;
    private readonly ITcpConnectionTable _tcpTable = new TcpConnectionTable();
    private ChainState? _lastChainState;
    private string? _chainStateError;
    private string? _checkResultKey;
    private object?[] _checkResultArgs = [];
    private string? _checkResultLiteral;
    private string _endpoints = "—";
    private string _activeConnections = "0";
    private long _totalConnections;
    private long _failedConnections;
    private Brush _failedBrush = Brushes.Gray;
    private string _tailscaleListeners = "";
    private string _tailscaleState = "";
    private Brush _tailscaleBrush = Brushes.Gray;
    private string _configPath = "";
    private bool _idleStopRunning;
    private bool _idleStopBusy;
    private string _idleStopState = "…";
    private string _idleStopAction = "";
    private Brush _idleStopBrush = Brushes.Gray;
    private string _chainSummary = "…";
    private Brush _chainStateBrush = Theme.TextMuted;
    private bool _chainOperationRunning;
    private bool _launchRunning;
    private string? _updateNoticeKey;
    private object?[] _updateNoticeArgs = [];

    public MainViewModel(ProxyRuntime runtime)
    {
        _runtime = runtime;
        Loc.LanguageChanged += OnLanguageChanged;
        // until the first liveness poll — the same word the not-running state uses
        IdleStopAction = Loc.S("Main.IdleStop.Button.Start");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Endpoints
    {
        get => _endpoints;
        private set => SetField(ref _endpoints, value);
    }

    public string ActiveConnections
    {
        get => _activeConnections;
        private set => SetField(ref _activeConnections, value);
    }

    public long TotalConnections
    {
        get => _totalConnections;
        private set => SetField(ref _totalConnections, value);
    }

    public long FailedConnections
    {
        get => _failedConnections;
        private set => SetField(ref _failedConnections, value);
    }

    /// <summary>The error counter only raises its voice when there is something to report.</summary>
    public Brush FailedBrush
    {
        get => _failedBrush;
        private set => SetField(ref _failedBrush, value);
    }

    /// <summary>The sidecar daemon's listeners: its HTTP and SOCKS5 ports, straight from the config.</summary>
    public string TailscaleListeners
    {
        get => _tailscaleListeners;
        private set => SetField(ref _tailscaleListeners, value);
    }

    /// <summary>State word of the daemon's listeners: both / partial / none / unknown.</summary>
    public string TailscaleState
    {
        get => _tailscaleState;
        private set => SetField(ref _tailscaleState, value);
    }

    public Brush TailscaleBrush
    {
        get => _tailscaleBrush;
        private set => SetField(ref _tailscaleBrush, value);
    }

    public string ConfigPath
    {
        get => _configPath;
        set => SetField(ref _configPath, value);
    }

    /// <summary>The idle stopper is running (a stopper process exists).</summary>
    public bool IdleStopRunning => _idleStopRunning;

    public string IdleStopState
    {
        get => _idleStopState;
        private set => SetField(ref _idleStopState, value);
    }

    /// <summary>The toggle button word: Start when no stopper is running, Stop when one is.</summary>
    public string IdleStopAction
    {
        get => _idleStopAction;
        private set => SetField(ref _idleStopAction, value);
    }

    public Brush IdleStopBrush
    {
        get => _idleStopBrush;
        private set => SetField(ref _idleStopBrush, value);
    }

    public bool CanIdleStop => !_idleStopBusy;

    /// <summary>Formatted from key+args on every read — survives a language change.</summary>
    public string CheckResult => _checkResultKey is null ? _checkResultLiteral ?? "" : Loc.F(_checkResultKey, _checkResultArgs);

    /// <summary>An error line is marked as one; every other result stays quiet.</summary>
    public Brush CheckResultBrush => _checkResultKey == "Main.ErrorFmt" ? Theme.Bad : Theme.TextSecondary;

    /// <summary>Ready-made text without localization (composite results of guard operations).</summary>
    public void SetCheckText(string text)
    {
        _checkResultKey = null;
        _checkResultLiteral = text;
        OnPropertyChanged(nameof(CheckResult));
        OnPropertyChanged(nameof(CheckResultBrush));
    }

    /// <summary>Signal about a Claude update found at startup; empty when there is no update.</summary>
    public string UpdateNotice => _updateNoticeKey is null ? "" : Loc.F(_updateNoticeKey, _updateNoticeArgs);

    public void SetUpdateNotice(string installedVersion, string? offeredVersion)
    {
        if (offeredVersion is null)
        {
            _updateNoticeKey = "Main.Update.Available";
            _updateNoticeArgs = [];
        }
        else
        {
            _updateNoticeKey = "Main.Update.AvailableFmt";
            _updateNoticeArgs = [installedVersion, offeredVersion];
        }

        OnPropertyChanged(nameof(UpdateNotice));
    }

    /// <summary>The reported state has stopped being true — the version is current, the line goes away.</summary>
    public void ClearUpdateNotice()
    {
        _updateNoticeKey = null;
        _updateNoticeArgs = [];
        OnPropertyChanged(nameof(UpdateNotice));
    }

    /// <summary>Bring-up is offered while something in the chain is not up; state unknown — enabled.</summary>
    public bool CanChainUp =>
        !_chainOperationRunning && !_launchRunning && (_lastChainState is null || !_lastChainState.IsFullyUp);

    /// <summary>The stop is offered while any chain component is up; state unknown — enabled.</summary>
    public bool CanChainDown =>
        !_chainOperationRunning && !_launchRunning && (_lastChainState is null || HasAnythingUp(_lastChainState));

    private static bool HasAnythingUp(ChainState state) =>
        state.DaemonProcessAlive
        || state.DaemonHttpListening
        || state.KeeperRunning
        || state.BackendState == "Running"
        || state.ProxyRunning;

    /// <summary>Summary word of the chain card; the rail itself is drawn by the window.</summary>
    public string ChainSummary
    {
        get => _chainSummary;
        private set => SetField(ref _chainSummary, value);
    }

    /// <summary>
    /// Colour of the collapsed card's one status dot: fully up — good, anything up — warning
    /// (a partial chain is present but not usable), nothing up — the neutral resting state.
    /// </summary>
    public Brush ChainStateBrush
    {
        get => _chainStateBrush;
        private set => SetField(ref _chainStateBrush, value);
    }

    public bool CanLaunch => !_chainOperationRunning && !_launchRunning;

    public bool CanGuard { get; private set; } = true;

    /// <summary>Busy state of guard operations (UAC/write): the card's buttons gray out during the action.</summary>
    public void SetGuardBusy(bool busy)
    {
        CanGuard = !busy;
        OnPropertyChanged(nameof(CanGuard));
    }

    public void SetLaunchBusy(bool busy)
    {
        _launchRunning = busy;
        if (busy)
        {
            ChainSummary = Loc.S("Main.Launch.Busy");
            ChainStateBrush = Theme.Accent;
        }

        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
    }

    public void SetChainBusy(bool busy)
    {
        _chainOperationRunning = busy;
        if (busy)
        {
            ChainSummary = Loc.S("Main.Chain.Busy");
            ChainStateBrush = Theme.Accent;
        }

        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
    }

    public void SetCheckResult(string key, params object?[] args)
    {
        _checkResultKey = key;
        _checkResultArgs = args;
        OnPropertyChanged(nameof(CheckResult));
        OnPropertyChanged(nameof(CheckResultBrush));
    }

    public void SetChainStateError(string message)
    {
        _chainStateError = message;
        _lastChainState = null; // the state is unknown — both chain buttons stay available
        ChainSummary = Loc.F("Main.Chain.StateUnknownFmt", message);
        ChainStateBrush = Theme.TextMuted;
        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
    }

    public void Refresh()
    {
        // the port is the truth: in detached mode the listener is held by the worker, the card shows it
        var proxy = _runtime.ProxyControl;
        Endpoints = proxy.IsRunning && proxy.ListenerEndpoints.Count > 0
            ? string.Join(", ", proxy.ListenerEndpoints)
            : "—";
        ActiveConnections = proxy.ActiveConnections.ToString();
        TotalConnections = proxy.TotalConnections;
        FailedConnections = proxy.FailedConnections;
        FailedBrush = FailedConnections > 0 ? Theme.Bad : Theme.TextSecondary;
        TailscaleListeners = $"{_runtime.Config.Chain.DaemonHttpListen}, {_runtime.Config.ForwardProxy.Socks5Upstream}";
        RefreshTailscaleState();
    }

    /// <summary>
    /// The daemon's listeners live in the sidecar process: the row just reports whether anything
    /// listens on its ports (a TCP table read, no connection is made). Silent listeners
    /// are the normal resting state of a stopped chain, so they stay neutral.
    /// </summary>
    private void RefreshTailscaleState()
    {
        try
        {
            var http = Socks5Client.ParseEndPoint(_runtime.Config.Chain.DaemonHttpListen).Port;
            var socks = Socks5Client.ParseEndPoint(_runtime.Config.ForwardProxy.Socks5Upstream).Port;
            var httpListening = _tcpTable.GetListeners(http).Count > 0;
            var socksListening = _tcpTable.GetListeners(socks).Count > 0;
            (TailscaleState, TailscaleBrush) = (httpListening, socksListening) switch
            {
                (true, true) => (Loc.S("Main.Chain.Daemon.Listening"), Theme.Ok),
                (false, false) => (Loc.S("Main.Chain.Daemon.NoListener"), Theme.Idle),
                _ => (Loc.S("Main.Listeners.Partial"), Theme.Warn),
            };
        }
        catch (Exception)
        {
            TailscaleState = Loc.S("Main.State.Unknown");
            TailscaleBrush = Theme.TextMuted;
        }
    }

    /// <summary>Liveness word of the idle stopper, from the process list; the button word follows the state.</summary>
    public void UpdateIdleStop(bool running)
    {
        _idleStopRunning = running;
        IdleStopState = running ? Loc.S("Main.IdleStop.Running") : Loc.S("Main.IdleStop.Off");
        IdleStopBrush = running ? Theme.Ok : Theme.TextSecondary;
        IdleStopAction = running ? Loc.S("Main.IdleStop.Button.Stop") : Loc.S("Main.IdleStop.Button.Start");
        OnPropertyChanged(nameof(IdleStopRunning));
    }

    /// <summary>A failed liveness search: the state word goes unknown, the last belief and the button stay.</summary>
    public void SetIdleStopUnknown()
    {
        IdleStopState = Loc.S("Main.State.Unknown");
        IdleStopBrush = Theme.TextMuted;
    }

    /// <summary>The toggle grays out while its operation runs (spawn + confirmation, or the kill).</summary>
    public void SetIdleStopBusy(bool busy)
    {
        _idleStopBusy = busy;
        OnPropertyChanged(nameof(CanIdleStop));
    }

    /// <summary>Chain snapshot: the summary word and the last state kept for a language change.</summary>
    public void UpdateChain(ChainState state)
    {
        _lastChainState = state;
        _chainStateError = null;
        ChainSummary = state.IsFullyUp ? Loc.S("Main.Chain.FullUp") : Loc.S("Main.Chain.Partial");
        ChainStateBrush = state.IsFullyUp ? Theme.Ok : HasAnythingUp(state) ? Theme.Warn : Theme.Idle;
        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
    }

    private void OnLanguageChanged()
    {
        Refresh();
        OnPropertyChanged(nameof(CheckResult));
        OnPropertyChanged(nameof(UpdateNotice));
        UpdateIdleStop(_idleStopRunning);
        if (_chainOperationRunning)
        {
            ChainSummary = Loc.S("Main.Chain.Busy");
        }
        else if (_launchRunning)
        {
            ChainSummary = Loc.S("Main.Launch.Busy");
        }
        else if (_chainStateError is not null)
        {
            ChainSummary = Loc.F("Main.Chain.StateUnknownFmt", _chainStateError);
        }
        else if (_lastChainState is not null)
        {
            UpdateChain(_lastChainState);
        }
    }

    public void Dispose() => Loc.LanguageChanged -= OnLanguageChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name!);
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
