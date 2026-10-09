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
/// the application to the tray; the real exit is "Exit" in the tray menu (or Windows session end).
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
        _tray.ExitRequested += ExitFromTray;

        if (launchOnStart)
        {
            window.LaunchFromShortcut();
        }

        if (launchBrowserOnStart)
        {
            window.LaunchBrowserFromShortcut();
        }

        _ = RunStartupUpdateCheckAsync(window);
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

    private void ExitFromTray()
    {
        _exiting = true;
        _tray?.Dispose();
        Shutdown();
    }

    /// <summary>
    /// Background Claude update check on startup (ui.checkUpdatesOnStart): read-only of
    /// versions and the AppX journal — without bringing up the chain and without UAC. If there
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
            if (check.Installed is null || check.UpToDate)
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
