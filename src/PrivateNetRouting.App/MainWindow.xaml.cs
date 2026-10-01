using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;

namespace PrivateNetRouting.App;

/// <summary>
/// Главное окно: статус forward-прокси и цепочки, управление. Состояние цепочки опрашивается
/// реже, чем счётчики прокси: каждый опрос — это CLI-вызов и CONNECT-roundtrip.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ProxyRuntime _runtime;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chainTimer;
    private bool _chainBusy;

    public MainWindow(ProxyRuntime runtime, string configPath)
    {
        InitializeComponent();
        _runtime = runtime;
        _viewModel = new MainViewModel { ConfigPath = configPath };
        DataContext = _viewModel;
        _viewModel.Refresh(runtime);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh(runtime);
        _refreshTimer.Start();

        _chainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _chainTimer.Tick += async (_, _) => await RefreshChainAsync();
        _chainTimer.Start();
        _ = RefreshChainAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _chainTimer.Stop();
        base.OnClosed(e);
    }

    private async Task RefreshChainAsync()
    {
        if (_chainBusy)
        {
            return;
        }

        try
        {
            var state = await _runtime.Chain.GetStateAsync(CancellationToken.None);
            _viewModel.UpdateChain(state);
        }
        catch (Exception ex)
        {
            _viewModel.ChainSummary = $"состояние неизвестно: {ex.Message}";
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        await _runtime.StartAsync(CancellationToken.None);
        _viewModel.Refresh(_runtime);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        await _runtime.StopAsync();
        _viewModel.Refresh(_runtime);
    }

    private async void OnCheckChain(object sender, RoutedEventArgs e)
    {
        _viewModel.CheckResult = "проверяю (CONNECT-roundtrip)...";
        var alive = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15));
        _viewModel.CheckResult = alive
            ? $"цепочка жива: CONNECT через {_runtime.Config.ForwardProxy.Listen} прошёл"
            : $"цепочка не отвечает: CONNECT через {_runtime.Config.ForwardProxy.Listen} не прошёл";
    }

    private async void OnChainUp(object sender, RoutedEventArgs e)
    {
        if (!await RunChainBusy(() => _runtime.Chain.EnsureRunningAsync(null, CancellationToken.None)))
        {
            return;
        }

        _viewModel.CheckResult = "цепочка поднята";
        await RefreshChainAsync();
    }

    private async void OnChainDown(object sender, RoutedEventArgs e)
    {
        if (!await RunChainBusy(() => _runtime.Chain.StopAsync(null, CancellationToken.None)))
        {
            return;
        }

        _viewModel.CheckResult = "цепочка остановлена";
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
        _viewModel.ChainSummary = "выполняется операция (подтверди UAC, если появится)...";
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            _viewModel.CheckResult = $"ошибка: {ex.Message}";
            return false;
        }
        finally
        {
            _chainBusy = false;
            _viewModel.SetChainBusy(false);
        }
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _stateText = "неизвестно";
    private Brush _stateBrush = Brushes.Gray;
    private string _endpoints = "—";
    private string _activeConnections = "0";
    private long _totalConnections;
    private long _failedConnections;
    private string _socksUpstream = "";
    private string _checkResult = "";
    private string _configPath = "";
    private string _logPath = "";
    private bool _canStart;
    private bool _canStop;
    private string _chainSummary = "…";
    private string _chainDetails = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StateText
    {
        get => _stateText;
        private set => SetField(ref _stateText, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        private set => SetField(ref _stateBrush, value);
    }

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

    public string SocksUpstream
    {
        get => _socksUpstream;
        private set => SetField(ref _socksUpstream, value);
    }

    public string ConfigPath
    {
        get => _configPath;
        set => SetField(ref _configPath, value);
    }

    public string LogPath
    {
        get => _logPath;
        private set => SetField(ref _logPath, value);
    }

    public string CheckResult
    {
        get => _checkResult;
        set => SetField(ref _checkResult, value);
    }

    public bool CanStart
    {
        get => _canStart;
        private set => SetField(ref _canStart, value);
    }

    public bool CanStop
    {
        get => _canStop;
        private set => SetField(ref _canStop, value);
    }

    public string ChainSummary
    {
        get => _chainSummary;
        set => SetField(ref _chainSummary, value);
    }

    public string ChainDetails
    {
        get => _chainDetails;
        private set => SetField(ref _chainDetails, value);
    }

    public bool CanChainUp => !_chainOperationRunning;

    public bool CanChainDown => !_chainOperationRunning;

    private bool _chainOperationRunning;

    public void SetChainBusy(bool busy)
    {
        _chainOperationRunning = busy;
        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
    }

    public void Refresh(ProxyRuntime runtime)
    {
        var proxy = runtime.Proxy;
        var running = proxy.IsRunning;
        StateText = running ? "работает" : "остановлен";
        StateBrush = running
            ? new SolidColorBrush(Color.FromRgb(0x7C, 0xB9, 0x7C))
            : new SolidColorBrush(Color.FromRgb(0xD9, 0x6B, 0x6B));
        Endpoints = running && proxy.ListenerEndpoints.Count > 0
            ? string.Join(", ", proxy.ListenerEndpoints)
            : "—";
        ActiveConnections = proxy.ActiveConnections.ToString();
        TotalConnections = proxy.TotalConnections;
        FailedConnections = proxy.FailedConnections;
        SocksUpstream = runtime.Config.ForwardProxy.Socks5Upstream;
        LogPath = runtime.Log.FilePath;
        CanStart = !running;
        CanStop = running;
    }

    public void UpdateChain(ChainState state)
    {
        ChainSummary = state.IsFullyUp ? "полностью поднята" : "не полностью";
        var daemon = state.DaemonProcessAlive
            ? state.DaemonHttpListening ? "слушает" : "без слушателя"
            : "нет";
        ChainDetails =
            $"демон: {daemon}    кипер: {(state.KeeperRunning ? "работает" : "нет")}    " +
            $"узел: {state.BackendState ?? "нет ответа"}\n" +
            $"прокси: {(state.ProxyRunning ? "работает" : "остановлен")}    здоровье: {(state.ProxyHealthy ? "живо" : "не прошло")}";
    }

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
