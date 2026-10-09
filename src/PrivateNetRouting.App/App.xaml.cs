using System.Globalization;
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

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activationSubscription;
    private ProxyRuntime? _runtime;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // detached proxy worker (spawned by the chain supervisor): no UI and no
        // single instance — lives until the chain shutdown kills it
        if (e.Args.Contains(ForwardProxySupervisor.WorkerMarker, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(ProxyWorker.RunAsync(CancellationToken.None).GetAwaiter().GetResult());
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

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing);
            existing?.Set();
            Shutdown();
            return;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activationSubscription = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            callBack: (_, _) => Dispatcher.BeginInvoke(ActivateMainWindow),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);
        SessionEnding += (_, _) =>
        {
            _exiting = true;
            Shutdown();
        };

        var config = AppConfigStore.LoadOrCreate();
        Loc.Apply(AppLanguage.Resolve(config.Ui.Language, CultureInfo.CurrentUICulture));
        _runtime = ProxyRuntime.FromConfig(config);
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

        _tray = new TrayIcon();
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
    /// written quietly to the updater log.
    /// </summary>
    private async Task RunStartupUpdateCheckAsync(MainWindow window)
    {
        if (_runtime is null || !_runtime.Config.Ui.CheckUpdatesOnStart)
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
        _activationSubscription?.Unregister(null);
        _activate?.Dispose();
        _tray?.Dispose();
        // runtime shutdown — on the pool: await continuations inside DisposeAsync must not
        // land in the dispatcher queue, whose thread is blocking here as well
        if (_runtime is not null)
        {
            Task.Run(() => _runtime.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
