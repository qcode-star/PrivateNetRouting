using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.AppUpdate;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.App;

/// <summary>
/// Main window: chain status and control, the UI language selector (drawn flag + short code),
/// the help menu of the "?" button (diagnostics, the logs folder, about).
/// The chain is the single lifecycle object of the window: the chain buttons bring it up
/// (idempotently — a partially up chain gets its missing links) and stop it. Chain state is polled
/// less often than the status rows: each poll is a CLI call and a CONNECT roundtrip. All strings
/// come from the Localization/Strings.*.xaml dictionaries: XAML takes them via DynamicResource,
/// code via Loc.S/Loc.F; a language change rebinds the dictionary on the fly (Loc.LanguageChanged),
/// the view model redraws its strings.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The chain stages in bring-up order — the order the rail draws them in; the mode picks the list.</summary>
    private static readonly string[] TailscaleComponents = ["Daemon", "Keeper", "Node", "Proxy", "Health"];

    /// <summary>The external-upstream modes (socks5, http): the links before the proxy collapse into one egress check.</summary>
    private static readonly string[] ExternalComponents = ["Egress", "Proxy", "Health"];

    /// <summary>The skipped shape (the http mode, or socks5 + forwardProxy.enabled=false): no local proxy stage at all.</summary>
    private static readonly string[] ExternalSkippedComponents = ["Egress", "Health"];

    private static readonly string[] TailscaleRailKeys =
        ["Main.Rail.Daemon", "Main.Rail.Keeper", "Main.Rail.Node", "Main.Rail.Proxy", "Main.Rail.Health"];
    private static readonly string[] ExternalRailKeys = ["Main.Rail.Egress", "Main.Rail.Proxy", "Main.Rail.Health"];
    private static readonly string[] ExternalSkippedRailKeys = ["Main.Rail.Egress", "Main.Rail.Health"];

    private readonly ProxyRuntime _runtime;
    private readonly MainViewModel _viewModel;
    private readonly ChainRail _rail;
    private readonly bool _externalUpstream;
    private readonly bool _skipProxy;
    private readonly string[] _chainComponents;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chainTimer;
    private readonly DispatcherTimer _guardTimer;
    private readonly DispatcherTimer _busyClock;
    private readonly Stopwatch _busyWatch = new();
    private ChainState? _lastChainState;
    private GuardSnapshot? _lastGuards;
    private bool _guardBusy;
    private int _chainEpoch; // bumped when an operation begins and ends: a snapshot taken across a bump is stale
    private int _chainRefreshSeq; // issue order of the chain snapshots
    private int _chainAppliedSeq; // the newest snapshot already painted — an older one finishing late is dropped
    private bool _chainPolling; // a timer poll is in flight — the next tick waits for it
    private int _guardEpoch;
    private int _guardRefreshSeq;
    private int _guardAppliedSeq;
    private bool _chainBusy;
    private bool _chainBusyCancellable; // the running chain operation is a bring-up — Stop cancels it
    private bool _chainBusyBringingUp; // the busy operation raises the chain — the rail speaks "starting", a stop speaks "stopping"
    private string? _openedAuthUrl; // the browser opens the login page once per URL
    private bool _launchBusy;
    private CancellationTokenSource? _operationCts; // cancels the running chain/launch operation
    private bool _fillingLanguages;
    private UpdateWindow? _updateWindow;
    private CodexUpdateWindow? _codexUpdateWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private AboutWindow? _aboutWindow;
    private bool _appUpdateChecking;
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
        _externalUpstream = runtime.Config.Chain.EffectiveMode != ChainMode.Tailscale;
        _skipProxy = AppProxyEndpoint.IsSkipped(runtime.Config);
        _chainComponents = _externalUpstream
            ? (_skipProxy ? ExternalSkippedComponents : ExternalComponents)
            : TailscaleComponents;
        _rail = new ChainRail(
            RailHost,
            _externalUpstream
                ? (_skipProxy ? ExternalSkippedRailKeys : ExternalRailKeys)
                : TailscaleRailKeys);
        UpstreamListenersLabel.Text = Loc.S(_externalUpstream ? "Main.EgressListeners" : "Main.TailscaleListeners");
        _rail.Reset(RailStage.Unknown);
        _viewModel.Refresh();
        SyncAutoStartBox();
        _chainCardExpanded = _runtime.Config.Ui.ChainCardExpanded;
        _guardsCardExpanded = _runtime.Config.Ui.GuardsCardExpanded;
        ApplyCardLayout();
        ApplyAppAvailability();
        FillLanguages(languagePreference);

        _busyClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _busyClock.Tick += (_, _) => BusyElapsed.Text = FormatElapsed(_busyWatch.Elapsed);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();
        _refreshTimer.Start();

        _chainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _chainTimer.Tick += async (_, _) =>
        {
            if (_chainPolling)
            {
                return; // the CLI timeout (20 s) outlives the tick (10 s) — do not stack polls
            }

            _chainPolling = true;
            try
            {
                await RefreshChainAsync();
                await RefreshIdleStopAsync();
            }
            finally
            {
                _chainPolling = false;
            }
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
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            // A maximize/restore cycle breaks SizeToContent: WPF keeps the content height measured
            // under the maximized bounds and afterwards ignores content changes (collapsing a card
            // no longer shrinks the window). Re-arming it re-measures against the restored size;
            // posted so the Win32 restore finishes before the window is resized.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ReArmSizeToContent);
        }
    }

    private void ReArmSizeToContent()
    {
        if (WindowState != WindowState.Normal)
        {
            return; // the state changed again while the callback waited — leave the new state alone
        }

        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.Height;
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
        UpstreamListenersLabel.Text = Loc.S(_externalUpstream ? "Main.EgressListeners" : "Main.TailscaleListeners");
        _rail.Relabel();
        if (_lastChainState is not null && !_chainBusy && !_launchBusy) // a live operation owns the rail
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

    /// <summary>
    /// The launch/update buttons of an application turned off in the config (claude.enabled /
    /// codex.enabled) leave the row: the owner sees only what they use. The browser button is
    /// shared and stays, leaning to the freed side: without Codex it takes the right edge,
    /// without Claude the left one, with both on or both off it keeps the centre.
    /// Follows a config reload.
    /// </summary>
    private void ApplyAppAvailability()
    {
        var claudeOn = _runtime.Config.Claude.Enabled;
        var codexOn = _runtime.Config.Codex.Enabled;
        var claude = claudeOn ? Visibility.Visible : Visibility.Collapsed;
        var codex = codexOn ? Visibility.Visible : Visibility.Collapsed;
        BtnLaunchClaude.Visibility = claude;
        BtnUpdateClaude.Visibility = claude;
        BtnUpdateCodex.Visibility = codex;
        BtnLaunchCodex.Visibility = codex;
        Grid.SetColumn(BtnLaunchBrowser, (claudeOn, codexOn) switch
        {
            (true, false) => 6,
            (false, true) => 0,
            _ => 3,
        });
    }

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

        var epoch = _chainEpoch;
        var seq = ++_chainRefreshSeq;
        try
        {
            var state = await _runtime.Chain.GetStateAsync(CancellationToken.None);
            if (_chainBusy || _launchBusy || epoch != _chainEpoch || seq < _chainAppliedSeq)
            {
                // an operation started (or started and finished) while the snapshot was in flight,
                // or a newer snapshot is already painted — this one is stale
                return;
            }

            _chainAppliedSeq = seq;
            _lastChainState = state;
            _viewModel.UpdateChain(state);
            RenderRail(state);
        }
        catch (Exception ex)
        {
            if (_chainBusy || _launchBusy || epoch != _chainEpoch || seq < _chainAppliedSeq)
            {
                return;
            }

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
        int proxyStage, healthStage;
        if (_externalUpstream)
        {
            _rail.SetStage(0, state.UpstreamReachable switch
            {
                true => new RailStage(Loc.S("Main.Chain.Egress.Reachable"), Theme.Ok),
                false => new RailStage(Loc.S("Main.Chain.Egress.NoRoute"), Theme.Bad),
                _ => new RailStage(Loc.S("Main.State.Unknown"), Theme.Idle),
            });

            if (_skipProxy)
            {
                // the skipped shape: the health stage follows the egress — no local proxy stage exists
                _rail.SetStage(1, state.UpstreamReachable == true
                    ? new RailStage(
                        state.ProxyHealthy ? Loc.S("Main.Chain.Health.Ok") : Loc.S("Main.Chain.Health.Fail"),
                        state.ProxyHealthy ? Theme.Ok : Theme.Bad)
                    : new RailStage(Loc.S("Main.Chain.Health.Idle"), Theme.Idle));
                return;
            }

            proxyStage = 1;
            healthStage = 2;
        }
        else
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
                : new RailStage(LocalizeBackendState(state.BackendState), state.BackendState == "Running" ? Theme.Ok : Theme.Warn));

            // the two closing stages are the same in both modes, only the indexes differ
            proxyStage = 3;
            healthStage = 4;
        }

        _rail.SetStage(proxyStage, new RailStage(
            state.ProxyRunning == true ? Loc.S("Main.Chain.Proxy.Running") : Loc.S("Main.Chain.Proxy.Stopped"),
            state.ProxyRunning == true ? Theme.Ok : Theme.Idle));

        _rail.SetStage(healthStage, state.ProxyRunning != true
            ? new RailStage(Loc.S("Main.Chain.Health.Idle"), Theme.Idle)
            : new RailStage(
                state.ProxyHealthy ? Loc.S("Main.Chain.Health.Ok") : Loc.S("Main.Chain.Health.Fail"),
                state.ProxyHealthy ? Theme.Ok : Theme.Bad));
    }

    /// <summary>
    /// A step from the orchestrator — a bring-up (the card's or a launch's) or the card's stop:
    /// the rail runs live instead of waiting for the next poll, the current step's detail sits
    /// under it, and the UAC hint appears with the daemon only when the start actually goes
    /// through elevation (chain.tailscale.elevation). A stop speaks its own vocabulary and paints its
    /// finished stages neutral: a grey "stopped" must not read as the green of a running stage.
    /// </summary>
    private void OnChainStep(ChainStep step)
    {
        var bringingUp = !_chainBusy || _chainBusyBringingUp; // a launch draws while the card is idle — it only ever brings up
        var stage = step.Status switch
        {
            ChainStepStatus.Active => new RailStage(
                Loc.S(bringingUp ? "Main.Rail.Starting" : "Main.Rail.Stopping"), Theme.Accent),
            ChainStepStatus.Done => bringingUp
                ? new RailStage(Loc.S("Main.Rail.Ready"), Theme.Ok, RailMark.Check)
                : new RailStage(Loc.S("Main.Rail.Stopped"), Theme.Idle, RailMark.Check),
            ChainStepStatus.Skipped => new RailStage(Loc.S("Main.Chain.Skipped"), Theme.Ok, RailMark.Check),
            ChainStepStatus.Failed => new RailStage(Loc.S("Main.Rail.Failed"), Theme.Bad, RailMark.Cross),
            _ => new RailStage(Loc.S("Main.Rail.Waiting"), Theme.Idle),
        };
        _rail.SetStage(Array.IndexOf(_chainComponents, step.Component), stage);

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

        if (step.Component == "Node")
        {
            if (!string.IsNullOrEmpty(step.AuthUrl) && step.AuthUrl != _openedAuthUrl)
            {
                _openedAuthUrl = step.AuthUrl;
                ChainHint.Text = Loc.S("Main.Chain.Hint.Login");
                ChainHint.Visibility = Visibility.Visible;
                try
                {
                    Process.Start(new ProcessStartInfo(step.AuthUrl) { UseShellExecute = true });
                    _runtime.Log.Info($"node login: the authentication page opened in the browser ({step.AuthUrl})");
                }
                catch (Exception ex)
                {
                    _runtime.Log.Warn($"node login: failed to open the browser: {ex.Message}");
                }
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
        _chainEpoch++;
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
        _chainEpoch++;
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

        var epoch = _guardEpoch;
        var seq = ++_guardRefreshSeq;
        try
        {
            var snapshot = await _runtime.Guard.GetStatusAsync(CancellationToken.None);
            if (_guardBusy || epoch != _guardEpoch || seq < _guardAppliedSeq)
            {
                return; // a guard operation ran meanwhile, or a newer status is already painted
            }

            _guardAppliedSeq = seq;
            _lastGuards = snapshot;
            _viewModel.SetGuardsKnown(true);
            RenderGuards();
        }
        catch (Exception ex)
        {
            if (_guardBusy || epoch != _guardEpoch || seq < _guardAppliedSeq)
            {
                return; // stale — the failure belongs to a status nobody paints any more
            }

            _lastGuards = null;
            _viewModel.SetGuardsKnown(false);
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

        SetGuardDots(
            snapshot.Ban.State,
            snapshot.Policy.State,
            GuardAggregate.SettingsLayer(snapshot),
            snapshot.Wsl.State);

        // a hosts block that lags behind the configured list carries a visible note at the end of the
        // line (the CLI spells the same marker); it is coloured as a warning and the tooltip says
        // what to do about it
        var staleHosts = snapshot.Ban.HostsEntries > 0 && !snapshot.Ban.HostsCurrent;
        var banDetail = snapshot.Ban.Detail ?? (snapshot.Ban.State switch
        {
            GuardState.Off => Loc.S("Main.Guard.Ban.Off"),
            GuardState.Unknown => Loc.S("Main.Guard.Unknown"),
            _ => Loc.F("Main.Guard.Ban.DetailFmt", snapshot.Ban.HostsEntries, snapshot.Ban.NrptNamespaces.Count, snapshot.Ban.FirewallRules),
        });
        BanDetail.Inlines.Clear();
        BanDetail.Inlines.Add(new Run(banDetail));
        if (staleHosts)
        {
            BanDetail.Inlines.Add(new Run(" " + Loc.S("Main.Guard.Ban.StaleMark")) { Foreground = Theme.Bad });
        }

        BanDetail.ToolTip = staleHosts ? Loc.S("Main.Guard.Ban.Stale") : null;

        PolicyDetail.Text = snapshot.Policy.State switch
        {
            GuardState.On => Loc.F("Main.Guard.Policy.DetailFmt", snapshot.Policy.CurrentUrl),
            GuardState.Off => Loc.S("Main.Guard.Policy.Off"),
            GuardState.Partial => Loc.F("Main.Guard.Policy.OtherFmt", snapshot.Policy.CurrentUrl, snapshot.Policy.ExpectedUrl),
            _ => snapshot.Policy.Detail ?? Loc.S("Main.Guard.Unknown"),
        };

        // one row, both applications: Claude Code env and the Codex layer (config.toml flag + .env)
        var claude = snapshot.Settings.State switch
        {
            GuardState.On => Loc.F("Main.Guard.Settings.OkFmt", snapshot.Settings.ExpectedProxy),
            GuardState.Off => Loc.S("Main.Guard.Settings.NoFile"),
            GuardState.Partial => Loc.F(
                "Main.Guard.Settings.MismatchFmt",
                snapshot.Settings.CurrentProxy ?? "—",
                snapshot.Settings.CurrentNoProxy ?? "—"),
            _ => snapshot.Settings.Detail ?? Loc.S("Main.Guard.Unknown"),
        };
        var codex = snapshot.CodexSettings.State switch
        {
            GuardState.On => Loc.S("Main.Guard.Settings.Codex.Ok"),
            GuardState.Off => Loc.S("Main.Guard.Settings.Codex.NoFile"),
            GuardState.Partial => string.Join("; ", new[]
            {
                snapshot.CodexSettings.Enabled is true
                    ? null
                    : Loc.F(
                        "Main.Guard.Settings.Codex.FlagFmt",
                        snapshot.CodexSettings.Enabled is { } enabled ? enabled.ToString().ToLowerInvariant() : Loc.S("Main.Guard.Settings.Codex.Unset")),
                snapshot.CodexSettings.EnvUpToDate ? null : Loc.S("Main.Guard.Settings.Codex.EnvStale"),
            }.Where(part => part is not null)),
            _ => snapshot.CodexSettings.Detail ?? Loc.S("Main.Guard.Unknown"),
        };
        SettingsDetail.Text = $"{claude}; {codex}";

        var wslText = snapshot.Wsl.Distros.Count == 0
            ? LocalizeWslReason(snapshot.Wsl.Reason)
            : string.Join("; ", snapshot.Wsl.Distros.Select(LocalizeDistroStatus));
        if (snapshot.Wsl.Warning is not null)
        {
            // a read-only environment note (e.g. a loopback-only applications proxy endpoint)
            wslText += wslText.Length > 0 ? "; " + Loc.S("Main.Guard.Wsl.Loopback") : Loc.S("Main.Guard.Wsl.Loopback");
        }

        WslDetail.Text = wslText;
        WslDetail.ToolTip = snapshot.Wsl.Distros.Count == 0
            ? null
            : string.Join("\n", snapshot.Wsl.Distros.Select(d => $"{d.Distro}: {d.Reason}"));

        RenderGuardSummary();
        UpdateGuardAllButton();
    }

    /// <summary>
    /// The collapsed card's one status line: the five layer states folded into one. Unknown
    /// dominates (nobody knows), any mix folds to partial; only all-on is good, all-off is the
    /// only bad one.
    /// </summary>
    private void RenderGuardSummary()
    {
        var state = _lastGuards is null ? GuardState.Unknown : GuardAggregate.Summary(_lastGuards);

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
    }

    /// <summary>
    /// The collapsed card's single action follows the state: everything on — "Disable all"
    /// (ban and policy; the sync layers have no "off"), anything less — "Enable all".
    /// </summary>
    private void UpdateGuardAllButton()
    {
        var allOn = _lastGuards is { } snapshot && GuardAggregate.AllOn(snapshot);
        GuardAllButton.Content = Loc.S(allOn ? "Main.Guard.Button.DisableAll" : "Main.Guard.Button.EnableAll");
    }

    /// <summary>
    /// The combined guard action of the collapsed card, one UAC prompt: ban and policy go as a
    /// single elevated command; when enabling, the no-elevation syncs (settings.json, WSL)
    /// follow in-process.
    /// </summary>
    private async void OnGuardAll(object sender, RoutedEventArgs e)
    {
        if (_lastGuards is null)
        {
            return; // the state is not read yet: the direction of the action is unknown
        }

        var disable = _lastGuards is { } snapshot && GuardAggregate.AllOn(snapshot);
        await RunGuardAsync(async () =>
        {
            var all = await _runtime.Guard.AllAsync(!disable, CancellationToken.None);
            var dns = LiveWord(all.Live.DnsBlocked);
            var direct = LiveWord(all.Live.DirectBlocked);
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
        GuardState.On => Loc.F("Main.Guard.Wsl.DistroOnFmt", distro.Distro, distro.ScriptPort, LocalizeCodexEnv(distro)),
        GuardState.Off => $"{distro.Distro}: {Loc.S("Main.Guard.State.Off")}",
        GuardState.Partial => Loc.F("Main.Guard.Wsl.DistroPartialFmt", distro.Distro, LocalizeCodexEnv(distro)),
        _ => $"{distro.Distro}: {LocalizeWslReason(distro.Reason)}",
    };

    /// <summary>The Codex proxy env layer of a distro in one word (the file carries the proxy port).</summary>
    private static string LocalizeCodexEnv(WslDistroStatus distro) => distro.ProxyEnvPresent
        ? Loc.F("Main.Guard.Wsl.CodexEnvOnFmt", distro.ProxyEnvPort ?? 0)
        : Loc.S("Main.Guard.Wsl.CodexEnvMissing");

    /// <summary>The node's tailscale backend state as a word of the UI language; an unknown state stays verbatim.</summary>
    private static string LocalizeBackendState(string backendState) => backendState switch
    {
        "Running" or "Stopped" or "Starting" or "NeedsLogin" or "NeedsMachineAuth" or "NoState" =>
            Loc.S($"Main.Chain.Node.{backendState}"),
        _ => backendState,
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

        if (reason.Contains("nothing to sync", StringComparison.Ordinal))
        {
            return Loc.S("Main.Guard.Wsl.NoSync");
        }

        // the auto mode's "WSL is not available (<error>)" reason carries the error and stays verbatim
        if (reason.Contains("none of the configured distros", StringComparison.Ordinal))
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

    /// <summary>A live ban verdict as a word: blocked / OPEN / not checked (no verdict).</summary>
    private static string LiveWord(bool? blocked) => Loc.S(blocked switch
    {
        true => "Main.Guard.Live.Blocked",
        false => "Main.Guard.Live.Open",
        null => "Main.Guard.Live.NotChecked",
    });

    /// <summary>Ban and policy go through the elevated helper (UAC); live checks — right after.</summary>
    private async void OnGuardBanOn(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.BanAsync(enable: true, CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Ban.LiveFmt",
            LiveWord(result.Live.DnsBlocked),
            LiveWord(result.Live.DirectBlocked));
    });

    private async void OnGuardBanOff(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.BanAsync(enable: false, CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Ban.LiveFmt",
            LiveWord(result.Live.DnsBlocked),
            LiveWord(result.Live.DirectBlocked));
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

    /// <summary>
    /// One sync for both applications: env into Claude Code settings.json, the system-proxy flag
    /// into Codex config.toml; a backup of either file is named in the result.
    /// </summary>
    private async void OnGuardSettingsSync(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.SyncSettingsAsync(CancellationToken.None);
        var backups = new[] { result.BackupPath, result.CodexBackupPath }
            .Where(path => path is not null)
            .ToList();
        var backupText = backups.Count == 0 ? Loc.S("Main.Guard.Backup.None") : string.Join("; ", backups);
        if (result.Written || result.CodexWritten)
        {
            _viewModel.SetCheckResult("Main.Guard.Settings.DoneFmt", result.ProxyUrl, backupText);
        }
        else
        {
            _viewModel.SetCheckResult("Main.Guard.Settings.UpToDateFmt", result.ProxyUrl);
        }
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
            : d.SettingsWritten || d.ScriptWritten || d.ProxyEnvWritten
                || d.BootRunnerWritten || d.UserCommandWritten || d.WslConfWritten
                ? Loc.F("Main.Guard.Wsl.DistroDoneFmt", d.Distro, d.ProxyUrl ?? "", LocalizeCodexWritten(d.ProxyEnvWritten))
                : Loc.F("Main.Guard.Wsl.DistroUnchangedFmt", d.Distro, d.ProxyUrl ?? "", LocalizeCodexWritten(d.ProxyEnvWritten)));
        _viewModel.SetCheckText(string.Join("; ", lines));
    });

    /// <summary>The Codex proxy env part of a sync result line ("codex env written" / "up to date").</summary>
    private static string LocalizeCodexWritten(bool written) => written
        ? Loc.S("Main.Guard.Wsl.CodexEnvWritten")
        : Loc.S("Main.Guard.Wsl.CodexEnvUnchanged");

    private async void OnRefreshGuards(object sender, RoutedEventArgs e) => await RefreshGuardsAsync();

    private async Task RunGuardAsync(Func<Task> action)
    {
        if (_guardBusy)
        {
            return;
        }

        _guardBusy = true;
        _guardEpoch++;
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
            _guardEpoch++;
            _viewModel.SetGuardBusy(false);
            await RefreshGuardsAsync();
        }
    }

    private async void OnChainUp(object sender, RoutedEventArgs e) => await ChainUpAsync();

    private async void OnChainDown(object sender, RoutedEventArgs e) => await ChainDownAsync();

    /// <summary>
    /// Chain bring-up, shared by the card button and the tray item: the card draws the steps
    /// (the window may be hidden), the tray enablement follows the same Can* flags. The card's
    /// Stop stays enabled meanwhile and cancels the bring-up (RunChainBusy brings down what got
    /// raised before the cancellation).
    /// </summary>
    public async Task ChainUpAsync()
    {
        if (_chainBusy || _launchBusy)
        {
            return;
        }

        if (await RunChainBusy(bringUp: true, ct => _runtime.Chain.EnsureRunningAsync(new Progress<ChainStep>(OnChainStep), ct)))
        {
            _viewModel.SetCheckResult("Main.Chain.UpDone");
        }

        // a cancelled or failed operation leaves the rail mid-picture — redraw from the real state
        await RefreshChainAsync();
    }

    /// <summary>
    /// Chain shutdown, shared by the card button and the tray item. While a bring-up or a launch
    /// is running the same button is the cancel: it aborts the operation; what happens to the
    /// half-raised chain is the running operation's business (a bring-up extinguishes itself, a
    /// launch extinguishes only the chain it raised itself).
    /// </summary>
    public async Task ChainDownAsync()
    {
        if (_chainBusy)
        {
            if (_chainBusyCancellable)
            {
                _operationCts?.Cancel();
            }

            return;
        }

        if (_launchBusy)
        {
            _operationCts?.Cancel();
            return;
        }

        if (await RunChainBusy(bringUp: false, ct => _runtime.Chain.StopAsync(new Progress<ChainStep>(OnChainStep), ct)))
        {
            _viewModel.SetCheckResult("Main.Chain.DownDone");
        }

        // a cancelled or failed operation leaves the rail mid-picture — redraw from the real state
        await RefreshChainAsync();
    }

    private async Task<bool> RunChainBusy(bool bringUp, Func<CancellationToken, Task> action)
    {
        if (_chainBusy)
        {
            return false;
        }

        _chainBusy = true;
        _chainBusyCancellable = bringUp;
        _chainBusyBringingUp = bringUp;
        _viewModel.SetChainBusy(true, bringUp);
        BeginOperation();
        var success = false;
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        try
        {
            await action(cts.Token);
            success = true;
            return true;
        }
        catch (OperationCanceledException) when (bringUp)
        {
            // Stop during a bring-up: cancel, then bring down whatever got raised before the
            // cancellation (in the external-upstream modes that is normally nothing — the egress
            // check runs first).
            // The quiet stop draws no rail steps; the next state poll redraws the card.
            _chainBusyCancellable = false;
            _viewModel.SetChainBusy(true, cancellable: false);
            _viewModel.SetCheckResult("Main.Chain.Cancelled");
            try
            {
                await _runtime.Chain.StopAsync(null, CancellationToken.None);
            }
            catch (Exception stopEx)
            {
                _viewModel.SetCheckResult("Main.ErrorFmt", stopEx.Message);
            }

            return false;
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
            return false;
        }
        finally
        {
            _operationCts = null;
            cts.Dispose();
            _chainBusy = false;
            _chainBusyCancellable = false;
            _chainBusyBringingUp = false;
            _viewModel.SetChainBusy(false);
            EndOperation();
            SettleChainCardAfterOperation(success);
            // a raise re-arms the stopper in the background (watchdog.autoStart) — wait for the
            // spawn so the row shows it immediately; a stop takes the stopper down with the
            // chain — the row must not wait for the next poll either
            await _runtime.WatchdogControl.LastAutoStart;
            await RefreshIdleStopAsync();
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
    /// UI language re-applies immediately. The forwardProxy and chain sections stay as the
    /// process was built with — when the file holds different values, the result line says so
    /// and the application restart applies them.
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
            ApplyAppAvailability();
            _tray?.ShowLaunchClaude(_runtime.Config.Claude.Enabled);
            var preference = _runtime.Config.Ui.Language;
            Loc.Apply(AppLanguage.Resolve(preference, CultureInfo.CurrentUICulture));
            FillLanguages(preference);
            _viewModel.SetCheckResult(_runtime.ProxyConfigNeedsRestart
                ? "Main.Config.Reloaded.RestartHint"
                : "Main.Config.Reloaded");
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
    /// drawn in the chain card (tunnel start, checks, then Claude with proxy flags). Claude turned
    /// off in the config (claude.enabled=false) — the window only, like an already running Claude.
    /// </summary>
    public async void LaunchFromShortcut()
    {
        if (!_runtime.Config.Claude.Enabled)
        {
            _viewModel.SetCheckResult("Main.Launch.ClaudeDisabled");
            return;
        }

        if (_chainBusy || _launchBusy)
        {
            return; // the same interlock the tray path applies: a launch must not race a chain operation
        }

        bool claudeRunning;
        try
        {
            claudeRunning = await Task.Run(() => _runtime.Updater.IsClaudeRunning());
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
        if (!_runtime.Config.Claude.Enabled)
        {
            _viewModel.SetCheckResult("Main.Launch.ClaudeDisabled");
            return;
        }

        if (_chainBusy || _launchBusy)
        {
            return;
        }

        await RunLaunchAsync();
    }

    /// <summary>
    /// The offer before a launch: a copy started outside the application is single-instance, so a
    /// new start only activates its window and the proxy flags never reach the processes that are
    /// already running. Yes closes that copy and starts it again through the proxy, No cancels
    /// the launch (nothing is closed, nothing is raised) — the log keeps the reason.
    /// </summary>
    private bool ConfirmRestartOfRunningApp(RunningAppInfo running, string appNameKey) =>
        LaunchPrompts.ConfirmRestart(this, running, appNameKey);

    /// <summary>The chain as it is right before a launch raises it (null when the snapshot fails).</summary>
    private async Task<ChainState?> SnapshotChainAsync()
    {
        try
        {
            return await _runtime.Chain.GetStateAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _runtime.Log.Warn($"chain snapshot before the launch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A cancelled launch extinguishes only what it raised itself: with a snapshot, the links that
    /// were already up stay; without one (the snapshot failed) the whole chain goes down.
    /// </summary>
    private Task RollBackRaisedChainAsync(ChainState? before) =>
        before is null
            ? _runtime.Chain.StopAsync(null, CancellationToken.None)
            : _runtime.Chain.StopRaisedAsync(before, null, CancellationToken.None);

    private async Task RunLaunchAsync()
    {
        // one chain/launch operation at a time: the launch writes the same _operationCts slot the
        // card's operations use, so a concurrent start would leave Stop cancelling the wrong one
        if (_chainBusy || _launchBusy)
        {
            return;
        }

        _launchBusy = true;
        _viewModel.SetLaunchBusy(true);
        var success = false;
        var chainWasDown = false; // the launch raises the chain only when the live CONNECT failed
        ChainState? chainBefore = null; // the links that existed before the raise: a cancel leaves them running
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        try
        {
            var running = await Task.Run(() => _runtime.Launcher.InspectRunning());
            if (running.State == RunningAppState.WithoutProxy && !ConfirmRestartOfRunningApp(running, "App.Name.Claude"))
            {
                _runtime.Launcher.NoteRestartDeclined();
                _viewModel.SetCheckResult("Main.Launch.RestartDeclined");
                return;
            }

            // the pre-check is cancellable too (Stop acts during it); the probe swallows a cancel as a
            // failed check, so the token is re-checked: a cancel here raised nothing to roll back
            var healthy = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            chainWasDown = !healthy;
            if (!healthy)
            {
                chainBefore = await SnapshotChainAsync();
                BeginOperation();
            }

            var progress = healthy ? null : new Progress<ChainStep>(OnChainStep);
            var result = await _runtime.Launcher.LaunchAsync(LaunchPrompts.OptionsFor(running), progress, cts.Token);
            _viewModel.SetCheckResult(
                result.RestartedExisting ? "Main.Launch.RestartedFmt" : "Main.Launch.DoneFmt",
                Math.Round(result.Elapsed.TotalSeconds, 1));
            success = true;
        }
        catch (OperationCanceledException)
        {
            // Stop during the launch: the launch aborts (Claude is not started); a chain the
            // launch raised itself is brought down, one that was already up keeps working.
            if (chainWasDown)
            {
                try
                {
                    await RollBackRaisedChainAsync(chainBefore);
                }
                catch (Exception stopEx)
                {
                    _viewModel.SetCheckResult("Main.ErrorFmt", stopEx.Message);
                }
            }

            _viewModel.SetCheckResult("Main.Launch.Cancelled");
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _operationCts = null;
            cts.Dispose();
            _launchBusy = false;
            _viewModel.SetLaunchBusy(false);
            EndOperation();
            SettleChainCardAfterOperation(success);
            await _runtime.WatchdogControl.LastAutoStart; // the raise re-arms the stopper — show it right away
            await RefreshChainAsync();
            await RefreshIdleStopAsync();
        }
    }

    /// <summary>
    /// Browser launch: the profile created once by the "?" menu goes straight to the chain's own
    /// egress, so the application brings nothing into its path — the profile only has to exist and
    /// the egress has to answer (a dead one in the tailscale mode is offered a chain bring-up, the
    /// chain card draws it). An already running instance of ours is not duplicated (a second
    /// Firefox on the same profile hits the profile lock) — a note in the result line.
    /// </summary>
    private async void OnLaunchBrowser(object sender, RoutedEventArgs e) => await RunBrowserLaunchAsync();

    /// <summary>
    /// Shortcut mode (App --launch-browser instead of the "Claude (browser)" shortcut): a browser
    /// with our profile is already running — just the application window; not running — a normal launch.
    /// </summary>
    public async void LaunchBrowserFromShortcut() => await RunBrowserLaunchAsync();

    private async Task RunBrowserLaunchAsync()
    {
        // one chain/launch operation at a time, and the flag goes up before the first await: two
        // quick signals must not both pass the guard while the first scan is in flight
        if (_chainBusy || _launchBusy)
        {
            return;
        }

        _launchBusy = true;
        _viewModel.SetLaunchBusy(true);
        try
        {
            await RunBrowserLaunchCoreAsync();
        }
        finally
        {
            _launchBusy = false;
            _viewModel.SetLaunchBusy(false);
        }
    }

    private async Task RunBrowserLaunchCoreAsync()
    {
        var success = false;
        var chainWasDown = false; // set by the confirmation: the launch raises the chain only then
        ChainState? chainBefore = null; // the links that existed before the raise: a cancel leaves them running
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        try
        {
            if (!_runtime.Browser.IsConfigured)
            {
                // The first click without a profile: a warning with an offer to create one right here.
                if (!OfferBrowserProfileCreation())
                {
                    _viewModel.SetCheckResult("Main.Browser.NotConfigured");
                    return;
                }

                if (!_runtime.Browser.IsConfigured)
                {
                    return; // the dialog was closed without creating anything
                }
            }

            bool browserRunning;
            try
            {
                browserRunning = await Task.Run(() => _runtime.Browser.IsOurBrowserRunning());
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

            // The question about an egress that did not answer, asked by the launcher before the
            // launch. When the egress is ours to raise (the forward proxy while the chain runs
            // it, the sidecar's listener in the tailscale mode), yes makes the launcher raise the
            // chain right after this answer (drawn in the card, exactly like a manual bring-up —
            // the card is prepared here, the bring-up itself runs in the launcher only) and the
            // launch goes on. Over an external upstream there is nothing to raise — the question
            // is only whether to start with an upstream that is down. The launcher's callback runs
            // off the UI thread, so the question is marshalled back here.
            var confirmDeadEgress = (CancellationToken _) => Dispatcher.InvokeAsync(async () =>
            {
                var egress = _runtime.Browser.EgressUrl;
                if (!_runtime.Browser.ChainOwnsEgress)
                {
                    return MessageBox.Show(
                        this,
                        Loc.F("Main.Browser.EgressDownProceedFmt", egress),
                        Loc.S("Main.Browser.EgressDownTitle"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes;
                }

                if (MessageBox.Show(
                        this,
                        Loc.F("Main.Browser.EgressDownRaiseFmt", egress),
                        Loc.S("Main.Browser.EgressDownTitle"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return false;
                }

                chainBefore = await SnapshotChainAsync();
                BeginOperation();
                chainWasDown = true;
                return true;
            }).Task.Unwrap();

            var options = BrowserLauncherOptions.Default with
            {
                ConfirmDeadEgress = confirmDeadEgress,
            };
            var result = await _runtime.Browser.LaunchAsync(options, new Progress<ChainStep>(OnChainStep), cts.Token);
            if (result.Outcome == BrowserLaunchOutcome.Cancelled)
            {
                _viewModel.SetCheckResult("Main.Browser.EgressDownDeclined");
            }
            else
            {
                _viewModel.SetCheckResult(
                    result.EgressResponded ? "Main.Browser.Done" : "Main.Browser.DoneOverDeadEgress");
                success = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Same contract as the Claude launch: abort, extinguish only a chain the launch
            // raised itself.
            if (chainWasDown)
            {
                try
                {
                    await RollBackRaisedChainAsync(chainBefore);
                }
                catch (Exception stopEx)
                {
                    _viewModel.SetCheckResult("Main.ErrorFmt", stopEx.Message);
                }
            }

            _viewModel.SetCheckResult("Main.Browser.Cancelled");
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _operationCts = null;
            cts.Dispose();
            EndOperation();
            SettleChainCardAfterOperation(success);
            await _runtime.WatchdogControl.LastAutoStart; // the raise re-arms the stopper — show it right away
            await RefreshChainAsync();
            await RefreshIdleStopAsync();
        }
    }

    /// <summary>
    /// The first click on "Browser" without a profile: a warning with an offer to create one, then
    /// the dialog itself. True — the profile exists after the dialog.
    /// </summary>
    private bool OfferBrowserProfileCreation()
    {
        var answer = MessageBox.Show(
            this,
            Loc.S("Main.Browser.CreateOfferFmt"),
            Loc.S("Main.Browser.CreateOfferTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes && ShowBrowserProfileDialog();
    }

    /// <summary>The creation dialog (modal, the same from the help menu and from the first click); true — the profile exists after it.</summary>
    private bool ShowBrowserProfileDialog()
    {
        var dialog = new BrowserProfileWindow(_runtime.Config, _runtime.BrowserProfile, _runtime.Browser.Log)
        {
            Owner = this,
        };
        var created = dialog.ShowDialog() == true;
        if (created && dialog.Result is { } result)
        {
            _viewModel.SetCheckResult(
                "Main.Browser.ProfileCreatedFmt",
                result.ProfileDir + (result.ShortcutPath.Length > 0 ? $" (+ {Path.GetFileName(result.ShortcutPath)})" : ""));
        }

        return created;
    }

    /// <summary>
    /// Codex launch: the same contract as the Claude launch — a live CONNECT, an idempotent
    /// chain bring-up drawn in the chain card, then the app with the proxy flags and env
    /// (the package-context launch). A copy started outside the launcher keeps its own flags
    /// until it is closed, so the owner is offered a restart first.
    /// </summary>
    private async void OnLaunchCodex(object sender, RoutedEventArgs e) => await RunCodexLaunchAsync();

    private async Task RunCodexLaunchAsync()
    {
        // the same single-operation rule as the Claude launch (one _operationCts slot)
        if (_chainBusy || _launchBusy)
        {
            return;
        }

        _launchBusy = true;
        _viewModel.SetLaunchBusy(true);
        var success = false;
        var chainWasDown = false; // the launch raises the chain only when the live CONNECT failed
        ChainState? chainBefore = null; // the links that existed before the raise: a cancel leaves them running
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        try
        {
            var running = await Task.Run(() => _runtime.CodexLauncher.InspectRunning());
            if (running.State == RunningAppState.WithoutProxy && !ConfirmRestartOfRunningApp(running, "App.Name.Codex"))
            {
                _runtime.CodexLauncher.NoteRestartDeclined();
                _viewModel.SetCheckResult("Main.Launch.RestartDeclined");
                return;
            }

            // the pre-check is cancellable too (Stop acts during it); the probe swallows a cancel as a
            // failed check, so the token is re-checked: a cancel here raised nothing to roll back
            var healthy = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            chainWasDown = !healthy;
            if (!healthy)
            {
                chainBefore = await SnapshotChainAsync();
                BeginOperation();
            }

            var progress = healthy ? null : new Progress<ChainStep>(OnChainStep);
            var result = await _runtime.CodexLauncher.LaunchAsync(LaunchPrompts.OptionsFor(running), progress, cts.Token);
            _viewModel.SetCheckResult(
                result.RestartedExisting ? "Main.Codex.RestartedFmt" : "Main.Codex.DoneFmt",
                Math.Round(result.Elapsed.TotalSeconds, 1));
            success = true;
        }
        catch (OperationCanceledException)
        {
            // Same contract as the Claude launch: abort, extinguish only a chain the launch
            // raised itself.
            if (chainWasDown)
            {
                try
                {
                    await RollBackRaisedChainAsync(chainBefore);
                }
                catch (Exception stopEx)
                {
                    _viewModel.SetCheckResult("Main.ErrorFmt", stopEx.Message);
                }
            }

            _viewModel.SetCheckResult("Main.Codex.Cancelled");
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _operationCts = null;
            cts.Dispose();
            _launchBusy = false;
            _viewModel.SetLaunchBusy(false);
            EndOperation();
            SettleChainCardAfterOperation(success);
            await _runtime.WatchdogControl.LastAutoStart; // the raise re-arms the stopper — show it right away
            await RefreshChainAsync();
            await RefreshIdleStopAsync();
        }
    }

    /// <summary>
    /// Signal about a Claude update found at startup: the badge dot on the update button.
    /// Returns the localized text — App uses it for the tray balloon as well.
    /// </summary>
    public string SetUpdateNotice(string installedVersion, string? offeredVersion)
    {
        _viewModel.SetUpdateNotice(installedVersion, offeredVersion);
        return offeredVersion is null
            ? Loc.S("Main.Update.Available")
            : Loc.F("Main.Update.AvailableFmt", installedVersion, offeredVersion);
    }

#if DEBUG
    /// <summary>Visual check of the update badge: fake versions, no feed touched (the --force-update-badge debug run).</summary>
    public void DebugShowUpdateBadge() => _viewModel.SetUpdateNotice("0.5.0", "0.6.0");
#endif

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
        _updateWindow.Closed += (_, _) =>
        {
            _updateWindow = null;
            // the chain the window raised went up under its own progress — the card must not
            // wait for the next poll to see it
            _ = RefreshChainAsync();
        };
        _updateWindow.Show();
    }

    /// <summary>
    /// The update window has resolved the version state (its own check, or a finished
    /// installation): the update button's badge follows it — a stale one is dropped,
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
    /// Codex update window (Microsoft Store): one per application, a repeated click activates
    /// the open one.
    /// </summary>
    private void OnUpdateCodex(object sender, RoutedEventArgs e)
    {
        if (_codexUpdateWindow is not null)
        {
            _codexUpdateWindow.Activate();
            return;
        }

        _codexUpdateWindow = new CodexUpdateWindow(_runtime) { Owner = this };
        _codexUpdateWindow.VersionStateResolved += OnCodexVersionStateResolved;
        _codexUpdateWindow.Closed += (_, _) => _codexUpdateWindow = null;
        _codexUpdateWindow.Show();
    }

    /// <summary>
    /// The Codex update window has resolved the version state: the Codex update button's badge
    /// follows it — a stale dot is dropped (the version is current or the Store is unreachable).
    /// </summary>
    private void OnCodexVersionStateResolved(CodexUpdateCheck check)
    {
        if (!check.UpdateAvailable)
        {
            _viewModel.ClearCodexUpdateNotice();
            return;
        }

        SetCodexUpdateNotice(check.Installed.Version, check.OfferedVersion);
    }

    /// <summary>
    /// Signal about a Codex update found at startup: the badge dot on the Codex update button.
    /// Returns the localized text — App uses it for the tray balloon as well. The offered version
    /// comes from OpenAI's manifest when it answered (it rides the live proxy only).
    /// </summary>
    public string SetCodexUpdateNotice(string installedVersion, string? offeredVersion)
    {
        _viewModel.SetCodexUpdateNotice(installedVersion, offeredVersion);
        return offeredVersion is null
            ? Loc.F("Main.CodexUpdate.AvailableFmt", installedVersion)
            : Loc.F("Main.CodexUpdate.AvailableVersionFmt", installedVersion, offeredVersion);
    }

    private void ClearCodexUpdateNotice() => _viewModel.ClearCodexUpdateNotice();

    /// <summary>
    /// The help menu of the header "?" button: diagnostics, the logs folder, about. Built on
    /// every open, so the words follow the current language. The menu hangs on the button's
    /// ContextMenu property — a menu held only by a local variable does not survive while open.
    /// </summary>
    private void OnHelp(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = HelpButton,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(MakeHelpItem("Main.Help.Diagnostics", OnDiagnostics));
        menu.Items.Add(MakeHelpItem("Main.Help.OpenLogs", OnOpenLogs));
        menu.Items.Add(MakeHelpItem("Main.Help.CreateBrowserProfile", OnCreateBrowserProfile));
        var checkUpdates = MakeHelpItem("Main.Help.CheckUpdates", OnCheckAppUpdates);
        checkUpdates.IsEnabled = !_appUpdateChecking; // the menu is rebuilt on every open, a running check must show
        menu.Items.Add(checkUpdates);
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeHelpItem("Main.Help.About", OnAbout));
        HelpButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Creation (or re-creation) of the browser profile from the help menu: the same dialog the
    /// first click on "Browser" offers. The dialog is modal — the profile is either written or not.
    /// </summary>
    private void OnCreateBrowserProfile(object sender, RoutedEventArgs e) => ShowBrowserProfileDialog();

    private static MenuItem MakeHelpItem(string key, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = Loc.S(key) };
        item.Click += onClick;
        return item;
    }

    /// <summary>Opens the logs folder in Explorer; failures land in the result line.</summary>
    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_runtime.Logs.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
    }

    /// <summary>The About window: modeless (it holds no state worth blocking the window for),
    /// one per application — a repeated click activates it.</summary>
    private void OnAbout(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow { Owner = this };
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    /// <summary>
    /// Diagnostics window, read-only: the live state summary; one per application,
    /// a repeated click activates the open one.
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

    /// <summary>
    /// App self-update check from the help menu: the repository's GitHub releases against the
    /// running version — no chain and no proxy needed. A newer release — an offer to open the
    /// releases page; up to date or failed — a message box. The item is disabled while checking.
    /// </summary>
    private async void OnCheckAppUpdates(object sender, RoutedEventArgs e)
    {
        if (_appUpdateChecking)
        {
            return;
        }

        _appUpdateChecking = true;
        var menuItem = sender as MenuItem;
        if (menuItem is not null)
        {
            menuItem.IsEnabled = false;
        }

        try
        {
            var check = await _runtime.AppUpdate.CheckAsync(CancellationToken.None);
            if (check.HasUpdate)
            {
                if (AskOpenReleases(check))
                {
                    OpenReleasesPage();
                }
            }
            else
            {
                MessageBox.Show(
                    this,
                    Loc.F("SelfUpdate.UpToDateFmt", check.Current),
                    Loc.S("SelfUpdate.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _runtime.AppUpdate.Log?.Warn($"self-update check failed: {ex.Message}");
            MessageBox.Show(
                this,
                Loc.F("SelfUpdate.FailedFmt", ex.Message),
                Loc.S("SelfUpdate.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _appUpdateChecking = false;
            if (menuItem is not null)
            {
                menuItem.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// The startup offer: a newer release — a question dialog, "yes" opens the releases page in
    /// the browser. Quiet when up to date; a failure is logged (nothing bothers the user on start).
    /// </summary>
    public async Task OfferAppUpdateAsync()
    {
        AppUpdateCheck check;
        try
        {
            check = await _runtime.AppUpdate.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _runtime.AppUpdate.Log?.Warn($"self-update check failed: {ex.Message}");
            return;
        }

        if (check.HasUpdate && AskOpenReleases(check))
        {
            OpenReleasesPage();
        }
    }

    private bool AskOpenReleases(AppUpdateCheck check)
    {
        var answer = MessageBox.Show(
            this,
            Loc.F("SelfUpdate.AvailableFmt", check.Latest, check.Current),
            Loc.S("SelfUpdate.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }

    private static void OpenReleasesPage() =>
        Process.Start(new ProcessStartInfo(AppUpdateChecker.ReleasesPageUrl) { UseShellExecute = true });

    /// <summary>
    /// Fills the language selector: a drawn flag + the short code per supported language — the
    /// config's "auto" is not an option, the language it resolves to is shown instead. Selection —
    /// by the resolved value, tooltips carry the full names. The items are plain data rendered
    /// by the item template.
    /// </summary>
    private void FillLanguages(string preference)
    {
        _fillingLanguages = true;
        var current = AppLanguage.Resolve(preference, CultureInfo.CurrentUICulture);
        var options = AppLanguage.Supported
            .Select(code => new LanguageOption(code, code, LanguageDisplayName(code)))
            .ToList();
        LanguageBox.ItemsSource = options;
        LanguageBox.SelectedItem = options.FirstOrDefault(option => option.Code == current);
        _fillingLanguages = false;
    }

    private static string LanguageDisplayName(string code)
    {
        var culture = CultureInfo.GetCultureInfo(code);
        return culture.TextInfo.ToTitleCase(culture.NativeName);
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingLanguages || LanguageBox.SelectedItem is not LanguageOption selected)
        {
            return;
        }

        // the options are the supported codes only, so a manual choice is always concrete:
        // the config stops saying "auto" from the moment the user picks a language
        var preference = selected.Code;
        try
        {
            Loc.Apply(preference);
            AppConfigStore.Update(config => config.Ui.Language = preference);
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
            return;
        }

        FillLanguages(preference);
        _viewModel.SetCheckResult("Main.LanguageSavedFmt", preference);
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
    private string _totalConnections = "0";
    private string _failedConnections = "0";
    private Brush _failedBrush = Theme.TextSecondary;
    private string _upstreamListeners = "";
    private string _upstreamState = "";
    private Brush _upstreamBrush = Theme.TextMuted;
    private string _configPath = "";
    private bool _idleStopRunning;
    private bool _idleStopBusy;
    private string _idleStopState = "…";
    private string _idleStopAction = "";
    private Brush _idleStopBrush = Theme.TextMuted;
    private string _chainSummary = "…";
    private Brush _chainStateBrush = Theme.TextMuted;
    private bool _chainOperationRunning;
    private bool _chainOperationCancellable;
    private bool _launchRunning;
    private string? _updateNoticeKey;
    private object?[] _updateNoticeArgs = [];
    private readonly bool _externalUpstream;
    private readonly bool _skipProxy;

    public MainViewModel(ProxyRuntime runtime)
    {
        _runtime = runtime;
        _externalUpstream = runtime.Config.Chain.EffectiveMode != ChainMode.Tailscale;
        _skipProxy = AppProxyEndpoint.IsSkipped(runtime.Config);
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

    public string TotalConnections
    {
        get => _totalConnections;
        private set => SetField(ref _totalConnections, value);
    }

    public string FailedConnections
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

    /// <summary>The upstream's listeners: in the tailscale mode the sidecar daemon's HTTP and SOCKS5
    /// ports, in the external-upstream modes the external proxy's address (the socks5 one with its
    /// user, never the password).</summary>
    public string UpstreamListeners
    {
        get => _upstreamListeners;
        private set => SetField(ref _upstreamListeners, value);
    }

    /// <summary>State word of the upstream: tailscale — listener presence, external modes — reachability.</summary>
    public string UpstreamState
    {
        get => _upstreamState;
        private set => SetField(ref _upstreamState, value);
    }

    public Brush UpstreamBrush
    {
        get => _upstreamBrush;
        private set => SetField(ref _upstreamBrush, value);
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

    /// <summary>
    /// The toggle grays out while its operation runs; Start is offered only over a live chain —
    /// the stopper exists to stop the chain, a stopped chain takes its stopper down (the
    /// orchestrator's chain-stopped hook). Stop stays available whenever a stopper is running,
    /// and an unknown state keeps the gate open — the same convention as the chain buttons.
    /// </summary>
    public bool CanIdleStop =>
        !_idleStopBusy && (_idleStopRunning || _lastChainState is null || _lastChainState.IsFullyUp);

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

    /// <summary>The accent dot on the update button — the only in-window announcement of a found update.</summary>
    public Visibility UpdateBadgeVisibility =>
        _updateNoticeKey is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The versions sit in the button tooltip while an update is announced; otherwise no tooltip at all.</summary>
    public string? UpdateBadgeToolTip => _updateNoticeKey is null ? null : Loc.F(_updateNoticeKey, _updateNoticeArgs);

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

        OnPropertyChanged(nameof(UpdateBadgeVisibility));
        OnPropertyChanged(nameof(UpdateBadgeToolTip));
    }

    /// <summary>The reported state has stopped being true — the version is current, the dot goes away.</summary>
    public void ClearUpdateNotice()
    {
        _updateNoticeKey = null;
        _updateNoticeArgs = [];
        OnPropertyChanged(nameof(UpdateBadgeVisibility));
        OnPropertyChanged(nameof(UpdateBadgeToolTip));
    }

    // --- Codex update badge: the offered version rides OpenAI's manifest and may be unknown ---

    private string? _codexUpdateInstalledVersion;
    private string? _codexUpdateOfferedVersion;

    /// <summary>The Codex brand dot on the Codex update button.</summary>
    public Visibility CodexUpdateBadgeVisibility =>
        _codexUpdateInstalledVersion is null ? Visibility.Collapsed : Visibility.Visible;

    public string? CodexUpdateBadgeToolTip => _codexUpdateInstalledVersion is null
        ? null
        : _codexUpdateOfferedVersion is null
            ? Loc.F("Main.CodexUpdate.AvailableFmt", _codexUpdateInstalledVersion)
            : Loc.F("Main.CodexUpdate.AvailableVersionFmt", _codexUpdateInstalledVersion, _codexUpdateOfferedVersion);

    public void SetCodexUpdateNotice(string installedVersion, string? offeredVersion)
    {
        _codexUpdateInstalledVersion = installedVersion;
        _codexUpdateOfferedVersion = offeredVersion;
        OnPropertyChanged(nameof(CodexUpdateBadgeVisibility));
        OnPropertyChanged(nameof(CodexUpdateBadgeToolTip));
    }

    public void ClearCodexUpdateNotice()
    {
        _codexUpdateInstalledVersion = null;
        _codexUpdateOfferedVersion = null;
        OnPropertyChanged(nameof(CodexUpdateBadgeVisibility));
        OnPropertyChanged(nameof(CodexUpdateBadgeToolTip));
    }

    /// <summary>Bring-up is offered while something in the chain is not up; state unknown — enabled.</summary>
    public bool CanChainUp =>
        !_chainOperationRunning && !_launchRunning && (_lastChainState is null || !_lastChainState.IsFullyUp);

    /// <summary>
    /// The stop is offered while any chain component is up (state unknown — enabled), and stays
    /// offered while a bring-up or a launch runs: then it is the cancel of the running operation.
    /// </summary>
    public bool CanChainDown
    {
        get
        {
            if (_chainOperationRunning)
            {
                return _chainOperationCancellable;
            }

            if (_launchRunning)
            {
                return true;
            }

            return _lastChainState is null || HasAnythingUp(_lastChainState);
        }
    }

    private static bool HasAnythingUp(ChainState state) =>
        state.UpstreamReachable is true
        || state.DaemonProcessAlive
        || state.DaemonHttpListening
        || state.KeeperRunning
        || state.BackendState == "Running"
        || state.ProxyRunning == true;

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

    private bool _guardsKnown;

    /// <summary>
    /// The combined guard action follows the state it flips, so it waits for the first status read
    /// (and for a readable one: after a failed read nobody knows whether "enable" or "disable" is meant).
    /// </summary>
    public bool CanGuardAll => CanGuard && _guardsKnown;

    /// <summary>Busy state of guard operations (UAC/write): the card's buttons gray out during the action.</summary>
    public void SetGuardBusy(bool busy)
    {
        CanGuard = !busy;
        OnPropertyChanged(nameof(CanGuard));
        OnPropertyChanged(nameof(CanGuardAll));
    }

    public void SetGuardsKnown(bool known)
    {
        _guardsKnown = known;
        OnPropertyChanged(nameof(CanGuardAll));
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

    public void SetChainBusy(bool busy, bool cancellable = false)
    {
        _chainOperationRunning = busy;
        _chainOperationCancellable = cancellable;
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
        OnPropertyChanged(nameof(CanIdleStop));
    }

    public void Refresh()
    {
        // the port is the truth: in detached mode the listener is held by the worker, the card shows it
        var proxy = _runtime.ProxyControl;
        if (_skipProxy)
        {
            // the local proxy does not exist in this shape — the row says what the clients get instead
            Endpoints = Loc.F("Main.Proxy.SkippedFmt", AppProxyEndpoint.Address(_runtime.Config));
        }
        else
        {
            Endpoints = proxy.IsRunning && proxy.ListenerEndpoints.Count > 0
                ? string.Join(", ", proxy.ListenerEndpoints)
                : "—";
        }

        // a detached worker holds the listener: its counters live in its own process, ours would read 0
        if (proxy.CountersAreLocal)
        {
            ActiveConnections = proxy.ActiveConnections.ToString();
            TotalConnections = proxy.TotalConnections.ToString();
            FailedConnections = proxy.FailedConnections.ToString();
            FailedBrush = proxy.FailedConnections > 0 ? Theme.Bad : Theme.TextSecondary;
        }
        else
        {
            ActiveConnections = TotalConnections = FailedConnections = "—";
            FailedBrush = Theme.TextSecondary;
        }

        if (_externalUpstream)
        {
            UpstreamListeners = _runtime.Config.Chain.EffectiveMode == ChainMode.Http
                ? _runtime.Config.Chain.Http.Upstream
                : WithUser(_runtime.Config.Chain.Socks5.Upstream, _runtime.Config.Chain.Socks5.User);
            RefreshEgressState();
        }
        else
        {
            UpstreamListeners = $"{_runtime.Config.Chain.Tailscale.HttpListen}, {_runtime.Config.Chain.Tailscale.SocksListen}";
            RefreshTailscaleState();
        }
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
            var http = Socks5Client.ParseEndPoint(_runtime.Config.Chain.Tailscale.HttpListen).Port;
            var socks = Socks5Client.ParseEndPoint(_runtime.Config.Chain.Tailscale.SocksListen).Port;
            var httpListening = _tcpTable.GetListeners(http).Count > 0;
            var socksListening = _tcpTable.GetListeners(socks).Count > 0;
            (UpstreamState, UpstreamBrush) = (httpListening, socksListening) switch
            {
                (true, true) => (Loc.S("Main.Chain.Daemon.Listening"), Theme.Ok),
                (false, false) => (Loc.S("Main.Chain.Daemon.NoListener"), Theme.Idle),
                _ => (Loc.S("Main.Listeners.Partial"), Theme.Warn),
            };
        }
        catch (Exception)
        {
            UpstreamState = Loc.S("Main.State.Unknown");
            UpstreamBrush = Theme.TextMuted;
        }
    }

    /// <summary>External upstream: the reachability word comes from the last chain snapshot.</summary>
    private void RefreshEgressState()
    {
        (_upstreamState, _upstreamBrush) = _lastChainState?.UpstreamReachable switch
        {
            true => (Loc.S("Main.Chain.Egress.Reachable"), Theme.Ok),
            false => (Loc.S("Main.Chain.Egress.NoRoute"), Theme.Bad),
            _ => (Loc.S("Main.State.Unknown"), Theme.TextMuted),
        };
        OnPropertyChanged(nameof(UpstreamState));
        OnPropertyChanged(nameof(UpstreamBrush));
    }

    private static string WithUser(string upstream, string user) =>
        string.IsNullOrEmpty(user) ? upstream : $"{user}@{upstream}";

    /// <summary>Liveness word of the idle stopper, from the process list; the button word follows the state.</summary>
    public void UpdateIdleStop(bool running)
    {
        _idleStopRunning = running;
        IdleStopState = running ? Loc.S("Main.IdleStop.Running") : Loc.S("Main.IdleStop.Off");
        IdleStopBrush = running ? Theme.Ok : Theme.TextSecondary;
        IdleStopAction = running ? Loc.S("Main.IdleStop.Button.Stop") : Loc.S("Main.IdleStop.Button.Start");
        OnPropertyChanged(nameof(IdleStopRunning));
        OnPropertyChanged(nameof(CanIdleStop));
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
        ChainSummary = state.IsFullyUp
            ? Loc.S("Main.Chain.FullUp")
            : state.IsStopped ? Loc.S("Main.Chain.Stopped") : Loc.S("Main.Chain.Partial");
        ChainStateBrush = state.IsFullyUp ? Theme.Ok : HasAnythingUp(state) ? Theme.Warn : Theme.Idle;
        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
        OnPropertyChanged(nameof(CanIdleStop));
    }

    private void OnLanguageChanged()
    {
        Refresh();
        OnPropertyChanged(nameof(CheckResult));
        OnPropertyChanged(nameof(UpdateBadgeToolTip));
        OnPropertyChanged(nameof(CodexUpdateBadgeToolTip));
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
