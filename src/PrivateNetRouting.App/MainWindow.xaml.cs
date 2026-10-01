using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PrivateNetRouting.Core;

namespace PrivateNetRouting.App;

/// <summary>Главное окно: статус forward-прокси, управление и проверка цепочки.</summary>
public partial class MainWindow : Window
{
    private readonly ProxyRuntime _runtime;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

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
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        base.OnClosed(e);
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
