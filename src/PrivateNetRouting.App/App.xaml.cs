using System.Windows;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.App;

/// <summary>
/// Точка входа: одиночный экземпляр (mutex + событие активации), сборка ProxyRuntime из
/// пользовательского конфига и запуск главного окна. Прокси стартует автоматически,
/// если включён в конфиге.
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
        _runtime = ProxyRuntime.FromConfig(config);
        if (config.ForwardProxy.Enabled)
        {
            _runtime.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        var window = new MainWindow(_runtime, AppConfigStore.GetDefaultConfigPath());
        MainWindow = window;
        window.Show();
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
