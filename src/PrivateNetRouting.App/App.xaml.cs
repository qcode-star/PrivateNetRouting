using System.Globalization;
using System.Windows;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.App;

/// <summary>
/// Точка входа: одиночный экземпляр (mutex + событие активации), сборка ProxyRuntime из
/// пользовательского конфига и запуск главного окна. Прокси стартует автоматически,
/// если включён в конфиге. Элевейтнутые helper-команды (--elevated-*) выполняются
/// до всего прочего и сразу завершают процесс (их запускает ElevatedRunner через runas).
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\PrivateNetRouting.SingleInstance";
    private const string ActivateEventName = @"Local\PrivateNetRouting.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activationSubscription;
    private ProxyRuntime? _runtime;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryRunElevatedHelper(e.Args, out var helperCode))
        {
            Shutdown(helperCode);
            return;
        }

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
                _runtime.Log.Error($"старт прокси не удался: {ex.Message}");
            }
        }

        var window = new MainWindow(_runtime, AppConfigStore.GetDefaultConfigPath(), config.Ui.Language);
        MainWindow = window;
        window.Show();
    }

    private static bool TryRunElevatedHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        var command = args.FirstOrDefault();
        if (command is not (ElevatedChainCommands.StartDaemon or ElevatedChainCommands.StopChain))
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

        exitCode = ElevatedChainCommands.Execute(command, resultFile);
        return true;
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationSubscription?.Unregister(null);
        _activate?.Dispose();
        _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
