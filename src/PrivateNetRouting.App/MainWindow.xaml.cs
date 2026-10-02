using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.App;

/// <summary>
/// Главное окно: статус forward-прокси и цепочки, управление, выбор языка интерфейса.
/// Состояние цепочки опрашивается реже, чем счётчики прокси: каждый опрос — это
/// CLI-вызов и CONNECT-roundtrip. Все строки — из словарей Localization/Strings.*.xaml:
/// XAML берёт их DynamicResource'ом, код — через Loc.S/Loc.F; смена языка перевязывает
/// словарь на лету (Loc.LanguageChanged), ViewModel перерисовывает свои строки.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ProxyRuntime _runtime;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chainTimer;
    private bool _chainBusy;
    private bool _fillingLanguages;

    public MainWindow(ProxyRuntime runtime, string configPath, string languagePreference)
    {
        InitializeComponent();
        _runtime = runtime;
        _viewModel = new MainViewModel(runtime) { ConfigPath = configPath };
        DataContext = _viewModel;
        _viewModel.Refresh();
        FillLanguages(languagePreference);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();
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
        _viewModel.Dispose();
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
            _viewModel.SetChainStateError(ex.Message);
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        await _runtime.StartAsync(CancellationToken.None);
        _viewModel.Refresh();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        await _runtime.StopAsync();
        _viewModel.Refresh();
    }

    private async void OnCheckChain(object sender, RoutedEventArgs e)
    {
        _viewModel.SetCheckResult("Main.Check.Running");
        var alive = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15));
        _viewModel.SetCheckResult(
            alive ? "Main.Check.AliveFmt" : "Main.Check.DeadFmt",
            _runtime.Config.ForwardProxy.Listen);
    }

    private async void OnChainUp(object sender, RoutedEventArgs e)
    {
        if (!await RunChainBusy(() => _runtime.Chain.EnsureRunningAsync(null, CancellationToken.None)))
        {
            return;
        }

        _viewModel.SetCheckResult("Main.Chain.UpDone");
        await RefreshChainAsync();
    }

    private async void OnChainDown(object sender, RoutedEventArgs e)
    {
        if (!await RunChainBusy(() => _runtime.Chain.StopAsync(null, CancellationToken.None)))
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
        try
        {
            await action();
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
        }
    }

    /// <summary>Наполняет селектор языка: авто + поддержанные; выбор — по значению конфига.</summary>
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
    private ChainState? _lastChainState;
    private string? _chainStateError;
    private string? _checkResultKey;
    private object?[] _checkResultArgs = [];
    private string _stateText = "";
    private Brush _stateBrush = Brushes.Gray;
    private string _endpoints = "—";
    private string _activeConnections = "0";
    private long _totalConnections;
    private long _failedConnections;
    private string _socksUpstream = "";
    private string _configPath = "";
    private string _logPath = "";
    private bool _canStart;
    private bool _canStop;
    private string _chainSummary = "…";
    private string _chainDetails = "";
    private bool _chainOperationRunning;

    public MainViewModel(ProxyRuntime runtime)
    {
        _runtime = runtime;
        Loc.LanguageChanged += OnLanguageChanged;
    }

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

    /// <summary>Форматируется из ключа+аргументов при каждом чтении — переживает смену языка.</summary>
    public string CheckResult => _checkResultKey is null ? "" : Loc.F(_checkResultKey, _checkResultArgs);

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
        private set => SetField(ref _chainSummary, value);
    }

    public string ChainDetails
    {
        get => _chainDetails;
        private set => SetField(ref _chainDetails, value);
    }

    public bool CanChainUp => !_chainOperationRunning;

    public bool CanChainDown => !_chainOperationRunning;

    public void SetChainBusy(bool busy)
    {
        _chainOperationRunning = busy;
        if (busy)
        {
            ChainSummary = Loc.S("Main.Chain.Busy");
        }

        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
    }

    public void SetCheckResult(string key, params object?[] args)
    {
        _checkResultKey = key;
        _checkResultArgs = args;
        OnPropertyChanged(nameof(CheckResult));
    }

    public void SetChainStateError(string message)
    {
        _chainStateError = message;
        ChainSummary = Loc.F("Main.Chain.StateUnknownFmt", message);
    }

    public void Refresh()
    {
        var proxy = _runtime.Proxy;
        var running = proxy.IsRunning;
        StateText = running ? Loc.S("Main.State.Running") : Loc.S("Main.State.Stopped");
        StateBrush = running
            ? new SolidColorBrush(Color.FromRgb(0x7C, 0xB9, 0x7C))
            : new SolidColorBrush(Color.FromRgb(0xD9, 0x6B, 0x6B));
        Endpoints = running && proxy.ListenerEndpoints.Count > 0
            ? string.Join(", ", proxy.ListenerEndpoints)
            : "—";
        ActiveConnections = proxy.ActiveConnections.ToString();
        TotalConnections = proxy.TotalConnections;
        FailedConnections = proxy.FailedConnections;
        SocksUpstream = _runtime.Config.ForwardProxy.Socks5Upstream;
        LogPath = _runtime.Log.FilePath;
        CanStart = !running;
        CanStop = running;
    }

    public void UpdateChain(ChainState state)
    {
        _lastChainState = state;
        _chainStateError = null;
        ChainSummary = state.IsFullyUp ? Loc.S("Main.Chain.FullUp") : Loc.S("Main.Chain.Partial");
        var daemon = !state.DaemonProcessAlive
            ? Loc.S("Main.Chain.Daemon.Down")
            : state.DaemonHttpListening ? Loc.S("Main.Chain.Daemon.Listening") : Loc.S("Main.Chain.Daemon.NoListener");
        ChainDetails = Loc.F(
            "Main.Chain.DetailsFmt",
            daemon,
            state.KeeperRunning ? Loc.S("Main.Chain.Keeper.Running") : Loc.S("Main.Chain.Keeper.Down"),
            state.BackendState ?? Loc.S("Main.Chain.Node.NoResponse"),
            state.ProxyRunning ? Loc.S("Main.Chain.Proxy.Running") : Loc.S("Main.Chain.Proxy.Stopped"),
            state.ProxyHealthy ? Loc.S("Main.Chain.Health.Ok") : Loc.S("Main.Chain.Health.Fail"));
    }

    private void OnLanguageChanged()
    {
        Refresh();
        OnPropertyChanged(nameof(CheckResult));
        if (_chainOperationRunning)
        {
            ChainSummary = Loc.S("Main.Chain.Busy");
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
