using System.Globalization;
using System.IO;
using System.Windows;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Updater;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.App;

/// <summary>
/// Entry point for GUI modes: normal (window + tray), shortcut mode (--launch: if Claude is
/// already running — window only, otherwise a window with the chain/Claude launch process) and
/// elevated helper commands (--elevated-*, executed before everything else and immediately
/// terminating the process — they are started by ElevatedRunner via runas), as well as the body
/// of the detached proxy worker (--proxy-worker, before the single-instance mutex and before the
/// UI — this is a second process). Single instance — mutex + activation event: a repeated launch
/// shows the already open (or minimized to tray) window. Closing and minimizing the window hides
/// the application to the tray; the real exit is only from the tray menu: "Exit" leaves the chain
/// running in the background (the in-process proxy is handed to a detached worker, the next start
/// sees the chain as it was), "Exit with full chain stop" brings the chain down first
/// (or Windows session end).
/// Opening the window raises nothing: the chain and the proxy come up only from explicit actions
/// (chain up, Claude/browser launch, update).
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\PrivateNetRouting.SingleInstance";
    private const string ActivateEventName = @"Local\PrivateNetRouting.Activate";
    private const string LaunchEventName = @"Local\PrivateNetRouting.Launch";
    private const string LaunchBrowserEventName = @"Local\PrivateNetRouting.LaunchBrowser";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private EventWaitHandle? _launchSignal;
    private EventWaitHandle? _launchBrowserSignal;
    private readonly List<RegisteredWaitHandle> _subscriptions = [];
    private ProxyRuntime? _runtime;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallGlobalExceptionHandlers();

        // detached proxy worker (spawned by the chain supervisor): no UI and no
        // single instance — lives until the chain shutdown kills it
        if (e.Args.Contains(ForwardProxySupervisor.WorkerMarker, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(ProxyWorker.RunAsync(CancellationToken.None, e.Args).GetAwaiter().GetResult());
            return;
        }

        // detached idle-stop worker (spawned by the watchdog supervisor): no UI and no single
        // instance — waits for the idle, stops the WSL Claude host and the chain, exits by itself;
        // it outlives the application that started it (the chain card toggle)
        if (e.Args.Contains(WatchdogSupervisor.WorkerMarker, StringComparer.OrdinalIgnoreCase))
        {
            // on the pool, without the Dispatcher context: the worker's awaits (the watchdog loop,
            // the runtime disposal) must not land in the dispatcher queue while the UI thread is
            // blocking here — the same pattern as the runtime start below
            Shutdown(Task.Run(() => WatchdogWorker.RunAsync(CancellationToken.None)).GetAwaiter().GetResult());
            return;
        }

        if (TryRunElevatedHelper(e.Args, out var helperCode))
        {
            Shutdown(helperCode);
            return;
        }

        var launchOnStart = e.Args.Contains("--launch", StringComparer.OrdinalIgnoreCase);
        var launchBrowserOnStart = e.Args.Contains("--launch-browser", StringComparer.OrdinalIgnoreCase);

        // the events are created (or opened) BEFORE the mutex is taken: a second instance that
        // lands while the first one is still starting finds the event and its signal is kept for
        // the first instance's wait (a named event lives while any handle to it is open, and the
        // first instance holds its handles from before it owns the mutex)
        _activate = CreateSignal(ActivateEventName);
        _launchSignal = CreateSignal(LaunchEventName);
        _launchBrowserSignal = CreateSignal(LaunchBrowserEventName);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            // hand the verb over to the running instance: a shortcut launch must start Claude or
            // the browser there, not just surface the window
            if (launchOnStart)
            {
                _launchSignal.Set();
            }

            if (launchBrowserOnStart)
            {
                _launchBrowserSignal.Set();
            }

            if (!launchOnStart && !launchBrowserOnStart)
            {
                _activate.Set();
            }

            Shutdown();
            return;
        }

        // the callbacks only queue work to the dispatcher, which runs it after OnStartup has
        // finished — the main window exists by then
        _subscriptions.Add(Subscribe(_activate, ActivateMainWindow));
        _subscriptions.Add(Subscribe(_launchSignal, () =>
        {
            ActivateMainWindow();
            (MainWindow as MainWindow)?.LaunchFromShortcut();
        }));
        _subscriptions.Add(Subscribe(_launchBrowserSignal, () =>
        {
            ActivateMainWindow();
            (MainWindow as MainWindow)?.LaunchBrowserFromShortcut();
        }));
        SessionEnding += (_, _) =>
        {
            _exiting = true;
            Shutdown();
        };

        AppConfig config;
        try
        {
            // the system language first: a startup error below has to be readable even when the
            // config (and with it the configured language) cannot be loaded
            Loc.Apply(AppLanguage.Resolve("auto", CultureInfo.CurrentUICulture));
            config = AppConfigStore.LoadOrCreate();
            Loc.Apply(AppLanguage.Resolve(config.Ui.Language, CultureInfo.CurrentUICulture));
            _runtime = ProxyRuntime.FromConfig(config);
        }
        catch (Exception ex)
        {
            // a malformed config or endpoint: without this the shortcut would just "do nothing"
            LogUnhandled("startup", ex);
            MessageBox.Show(
                Loc.F("App.Error.StartupFmt", ex.Message, AppConfigStore.GetDefaultConfigPath()),
                Loc.S("App.Error.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // opening the window raises nothing: without the chain the proxy is unusable (its SOCKS5
        // upstream is down), so an auto-start would only produce a guaranteed-failing CONNECT
        // check; the proxy comes up as part of the chain (chain up / launches / update)

        var window = new MainWindow(_runtime, AppConfigStore.GetDefaultConfigPath(), config.Ui.Language);
        MainWindow = window;
        window.Closing += (_, e2) =>
        {
            if (_exiting)
            {
                return; // real exit: the window closes, OnExit will stop everything
            }

            e2.Cancel = true;
            window.Hide();
        };
        window.Show();

#if DEBUG
        // visual-check flag: the update badge with fake versions, without touching the feed
        if (e.Args.Contains("--force-update-badge"))
        {
            window.DebugShowUpdateBadge();
        }
#endif

        _tray = new TrayIcon();
        _tray.ShowLaunchClaude(config.Claude.Enabled);
        _tray.OpenRequested += ActivateMainWindow;
        _tray.ChainUpRequested += () => _ = window.ChainUpAsync();
        _tray.ChainDownRequested += () => _ = window.ChainDownAsync();
        _tray.LaunchClaudeRequested += () => _ = window.LaunchClaudeAsync();
        _tray.ExitRequested += () => _ = ExitFromTrayAsync(stopChain: false);
        _tray.ExitAndStopChainRequested += () => _ = ExitFromTrayAsync(stopChain: true);
        window.AttachTray(_tray);

        if (launchOnStart)
        {
            window.LaunchFromShortcut();
        }

        if (launchBrowserOnStart)
        {
            window.LaunchBrowserFromShortcut();
        }

        _ = RunStartupUpdateCheckAsync(window);
        _ = RunStartupCodexUpdateCheckAsync(window);
        _ = RunStartupAppUpdateCheckAsync(window);
    }

    private static bool TryRunElevatedHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        var command = args.FirstOrDefault() ?? "";
        if (command is not (ElevatedChainCommands.StartDaemon or ElevatedChainCommands.StopChain)
            && !GuardElevatedCommands.IsCommand(command))
        {
            return false;
        }

        string? resultFile = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--result-file")
            {
                resultFile = args[i + 1];
                break;
            }
        }

        if (resultFile is null)
        {
            return false;
        }

        // on the pool, without the Dispatcher context: the guard command awaits PowerShell
        // (NRPT/firewall) and the chain command awaits the sidecar CLI — their continuations must
        // not land in this blocked dispatcher queue (the same pattern as the runtime start below);
        // a blocking wait on the UI thread + a captured dispatcher context = a deadlock and an
        // eternally alive helper that never writes the result file
        var command1 = command;
        exitCode = Task.Run(() => GuardElevatedCommands.IsCommand(command1)
                ? GuardElevatedCommands.Execute(command1, resultFile)
                : ElevatedChainCommands.Execute(command1, resultFile))
            .GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// Safety net for everything a handler does not catch itself. An exception escaping a UI
    /// handler would otherwise end the process — together with the in-process forward proxy (all
    /// Claude/Codex traffic drops) — and leave a ghost tray icon. The dispatcher exception is
    /// logged, shown once, and swallowed: the application keeps running. Exceptions the runtime
    /// cannot let us survive (a crash on another thread) are logged before the process goes.
    /// </summary>
    private void InstallGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogUnhandled("ui", args.Exception);
            args.Handled = true;
            ShowUnhandledOnce(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogUnhandled("domain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogUnhandled("task", args.Exception);
            args.SetObserved();
        };
    }

    private const int ErrorDialogMinGapSeconds = 60;
    private bool _errorDialogOpen;
    private DateTime _lastErrorDialog = DateTime.MinValue;

    /// <summary>One dialog at a time and at most one a minute: a timer-driven handler that throws on every tick must not bury the owner in dialogs (every occurrence is in the log).</summary>
    private void ShowUnhandledOnce(Exception ex)
    {
        if (_errorDialogOpen || (DateTime.UtcNow - _lastErrorDialog).TotalSeconds < ErrorDialogMinGapSeconds)
        {
            return;
        }

        _errorDialogOpen = true;
        try
        {
            MessageBox.Show(
                Loc.F("App.Error.UnhandledFmt", ex.Message),
                Loc.S("App.Error.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _errorDialogOpen = false;
            _lastErrorDialog = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// To the application log when the runtime exists, otherwise (a startup failure, a crash
    /// before the runtime is built) to a small fallback file in the default log folder. Never throws.
    /// </summary>
    private void LogUnhandled(string source, Exception? ex)
    {
        var text = $"unhandled exception ({source}): {ex}";
        try
        {
            if (_runtime is not null)
            {
                _runtime.Log.Error(text);
                return;
            }

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateNetRouting", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "app-crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // the safety net itself must not fail the application
        }
    }

    private static EventWaitHandle CreateSignal(string name) =>
        new(false, EventResetMode.AutoReset, name);

    private RegisteredWaitHandle Subscribe(EventWaitHandle signal, Action action) =>
        ThreadPool.RegisterWaitForSingleObject(
            signal,
            callBack: (_, _) => Dispatcher.BeginInvoke(action),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);

    private void ActivateMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    /// <summary>
    /// Tray exit. "Exit" leaves the chain as it is: daemon, keeper and node are detached
    /// processes anyway, and the in-process forward proxy (if it is the one holding the port)
    /// is handed over to a detached worker — the chain keeps working in the background and the
    /// next GUI start sees it. "Exit with full chain stop" brings the whole chain down first
    /// (a UAC prompt is possible for the elevated daemon). A failure of either is logged and
    /// the exit continues — the user has already asked to leave.
    /// </summary>
    private async Task ExitFromTrayAsync(bool stopChain)
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        var runtime = _runtime;
        if (runtime is not null)
        {
            try
            {
                if (stopChain)
                {
                    await runtime.Chain.StopAsync(null, CancellationToken.None);
                }
                else
                {
                    await runtime.ProxyControl.DetachAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                runtime.Log.Error($"exit: {(stopChain ? "chain stop" : "proxy detach")} failed: {ex.Message}");
            }
        }

        Shutdown();
    }

    /// <summary>
    /// Background Claude update check on startup (ui.checkUpdatesOnStart): read-only of
    /// versions, the update feed (only when the proxy is already alive) and the AppX journal —
    /// without bringing up the chain and without UAC. If there
    /// is something to install — a line in the main window and a tray balloon; a failure is
    /// written quietly to the updater log. Not run for an app turned off (claude.enabled=false).
    /// </summary>
    private async Task RunStartupUpdateCheckAsync(MainWindow window)
    {
        if (_runtime is null || !_runtime.Config.Ui.CheckUpdatesOnStart || !_runtime.Config.Claude.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var check = await _runtime.Updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);
            // an update is announced only with a concrete offered version: with the chain down
            // the feed is unreachable and the journal may be silent — "no address known" is not
            // "an update exists", and claiming one contradicts the updater's own "up to date"
            if (check.Installed is null || check.UpToDate || check.OfferedVersion is null)
            {
                return;
            }

            var text = window.SetUpdateNotice(check.Installed.Version, check.OfferedVersion);
            _tray?.ShowNotice(text);
        }
        catch (Exception ex)
        {
            _runtime.Updater.Log.Warn($"startup update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Background Codex update check on startup (ui.checkUpdatesOnStart): the Microsoft Store
    /// catalog answer is read-only — no chain, no UAC, the Store CDN is not part of the ban.
    /// An update — a dot on the Codex update button and a tray balloon; the Store being
    /// unreachable is not announced (the dot just stays off). Runs a beat behind the Claude
    /// check so two balloons do not fight for the tray. Not run for an app turned off
    /// (codex.enabled=false).
    /// </summary>
    private async Task RunStartupCodexUpdateCheckAsync(MainWindow window)
    {
        if (_runtime is null || !_runtime.Config.Ui.CheckUpdatesOnStart || !_runtime.Config.Codex.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            var check = await _runtime.CodexUpdater.CheckAsync(CancellationToken.None);
            if (!check.UpdateAvailable)
            {
                return;
            }

            var text = window.SetCodexUpdateNotice(check.Installed.Version, check.OfferedVersion);
            _tray?.ShowNotice(text);
        }
        catch (Exception ex)
        {
            _runtime.CodexUpdater.Log.Warn($"startup Codex update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One-time app self-update check on start (ui.checkAppUpdatesOnStart): the repository's
    /// GitHub releases against the running version — no chain and no proxy needed. A newer
    /// release — an offer to open the releases page; a failure is logged quietly.
    /// </summary>
    private async Task RunStartupAppUpdateCheckAsync(MainWindow window)
    {
        if (_runtime is null || !_runtime.Config.Ui.CheckAppUpdatesOnStart)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await window.OfferAppUpdateAsync();
        }
        catch (Exception ex)
        {
            _runtime.AppUpdate.Log?.Warn($"startup self-update check failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Unregister(null);
        }

        _tray?.Dispose();
        // runtime shutdown — on the pool: await continuations inside DisposeAsync must not
        // land in the dispatcher queue, whose thread is blocking here as well
        if (_runtime is not null)
        {
            Task.Run(() => _runtime.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        // the mutex goes first, the events last: a launch signalled while this instance still
        // owned the mutex finds the event alive
        _singleInstance?.Dispose();
        _activate?.Dispose();
        _launchSignal?.Dispose();
        _launchBrowserSignal?.Dispose();
        base.OnExit(e);
    }
}
