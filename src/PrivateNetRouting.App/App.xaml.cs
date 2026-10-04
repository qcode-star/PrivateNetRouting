using System.Globalization;
using System.Windows;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.App;

/// <summary>
/// Точка входа GUI-режимов: обычный (окно + трей), ярлыковый (--launch: если Claude уже
/// запущен — только окно, иначе окно с процессом запуска цепочки/Claude) и элевейтнутые
/// helper-команды (--elevated-*, выполняются до всего прочего и сразу завершают процесс —
/// их запускает ElevatedRunner через runas), а также тело detached-воркера прокси
/// (--proxy-worker, до mutex одиночного экземпляра и до UI — это второй процесс).
/// Одиночный экземпляр — mutex + событие активации: повторный запуск показывает уже
/// открытое (или свёрнутое в трей) окно. Закрытие окна и минимизация прячут приложение
/// в трей; настоящий выход — «Выход» в меню трея (или завершение сеанса Windows).
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

        // detached-воркер прокси (spawnится супервизором цепочки): никакого UI и
        // одиночного экземпляра — живёт, пока его не убьёт останов цепочки
        if (e.Args.Contains(ForwardProxySupervisor.WorkerMarker, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(ProxyWorker.RunAsync(CancellationToken.None).GetAwaiter().GetResult());
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
        if (config.ForwardProxy.Enabled)
        {
            try
            {
                _runtime.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _runtime.Log.Error($"proxy start failed: {ex.Message}");
            }
        }

        var window = new MainWindow(_runtime, AppConfigStore.GetDefaultConfigPath(), config.Ui.Language);
        MainWindow = window;
        window.Closing += (_, e2) =>
        {
            if (_exiting)
            {
                return; // настоящий выход: окно закрывается, OnExit остановит всё
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

        exitCode = GuardElevatedCommands.IsCommand(command)
            ? GuardElevatedCommands.Execute(command, resultFile)
            : ElevatedChainCommands.Execute(command, resultFile);
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
    /// Фоновая проверка обновления Claude на старте (ui.checkUpdatesOnStart): только чтение
    /// версий и журнала AppX — без подъёма цепочки и без UAC. Есть что ставить — строка
    /// в главном окне и балун трея; сбой тихо пишется в лог обновлятора.
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
        _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
