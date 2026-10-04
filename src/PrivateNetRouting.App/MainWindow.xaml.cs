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
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Launcher;

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
    private readonly DispatcherTimer _guardTimer;
    private GuardSnapshot? _lastGuards;
    private bool _guardBusy;
    private bool _chainBusy;
    private bool _launchBusy;
    private bool _fillingLanguages;
    private UpdateWindow? _updateWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private CancellationTokenSource? _watchdogCts;
    private bool _watchdogSyncing;

    public MainWindow(ProxyRuntime runtime, string configPath, string languagePreference)
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
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

        // статусы гардов — только чтение (реестр/hosts/NRPT/firewall/WSL), но NRPT и firewall
        // читаются командлетами PowerShell — опрос реже, чем состояние цепочки
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _guardTimer.Tick += async (_, _) => await RefreshGuardsAsync();
        _guardTimer.Start();
        Loc.LanguageChanged += RenderGuardsFromCache;
        _ = RefreshGuardsAsync();
    }

    /// <summary>Минимизация прячется в трей (приложение продолжает работать) — как и закрытие окна.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _chainTimer.Stop();
        _guardTimer.Stop();
        Loc.LanguageChanged -= RenderGuardsFromCache;
        _watchdogCts?.Cancel();
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

    /// <summary>
    /// Гарды прямого доступа: статусы читаются целиком (реестр, hosts, NRPT/firewall,
    /// settings.json, WSL) и раскрашивают карточку; «Обновить» и таймер — тот же путь.
    /// </summary>
    private async Task RefreshGuardsAsync()
    {
        if (_guardBusy)
        {
            return;
        }

        try
        {
            _lastGuards = await _runtime.Guard.GetStatusAsync(CancellationToken.None);
            RenderGuards();
        }
        catch (Exception ex)
        {
            _lastGuards = null;
            BanDetail.Text = PolicyDetail.Text = SettingsDetail.Text = WslDetail.Text = ex.Message;
            SetGuardDots(GuardState.Unknown, GuardState.Unknown, GuardState.Unknown, GuardState.Unknown);
        }
    }

    private void RenderGuardsFromCache() => RenderGuards();

    private void RenderGuards()
    {
        var snapshot = _lastGuards;
        if (snapshot is null)
        {
            return;
        }

        SetGuardDots(snapshot.Ban.State, snapshot.Policy.State, snapshot.Settings.State, snapshot.Wsl.State);

        BanDetail.Text = snapshot.Ban.Detail ?? (snapshot.Ban.State switch
        {
            GuardState.Off => Loc.S("Main.Guard.Ban.Off"),
            GuardState.Unknown => Loc.S("Main.Guard.Unknown"),
            _ => Loc.F("Main.Guard.Ban.DetailFmt", snapshot.Ban.HostsEntries, snapshot.Ban.NrptNamespaces.Count, snapshot.Ban.FirewallRules),
        });
        BanDetail.ToolTip = snapshot.Ban.HostsEntries > 0 && !snapshot.Ban.HostsCurrent
            ? Loc.S("Main.Guard.Ban.Stale")
            : null;

        PolicyDetail.Text = snapshot.Policy.State switch
        {
            GuardState.On => Loc.F("Main.Guard.Policy.DetailFmt", snapshot.Policy.CurrentUrl),
            GuardState.Off => Loc.S("Main.Guard.Policy.Off"),
            GuardState.Partial => Loc.F("Main.Guard.Policy.OtherFmt", snapshot.Policy.CurrentUrl, snapshot.Policy.ExpectedUrl),
            _ => snapshot.Policy.Detail ?? Loc.S("Main.Guard.Unknown"),
        };

        SettingsDetail.Text = snapshot.Settings.State switch
        {
            GuardState.On => Loc.F("Main.Guard.Settings.OkFmt", snapshot.Settings.ExpectedProxy),
            GuardState.Off => Loc.S("Main.Guard.Settings.NoFile"),
            GuardState.Partial => Loc.F(
                "Main.Guard.Settings.MismatchFmt",
                snapshot.Settings.CurrentProxy ?? "—",
                snapshot.Settings.CurrentNoProxy ?? "—"),
            _ => snapshot.Settings.Detail ?? Loc.S("Main.Guard.Unknown"),
        };

        WslDetail.Text = snapshot.Wsl.Distros.Count == 0
            ? LocalizeWslReason(snapshot.Wsl.Reason)
            : string.Join("; ", snapshot.Wsl.Distros.Select(LocalizeDistroStatus));
        WslDetail.ToolTip = snapshot.Wsl.Distros.Count == 0
            ? null
            : string.Join("\n", snapshot.Wsl.Distros.Select(d => $"{d.Distro}: {d.Reason}"));
    }

    /// <summary>Строка одного дистрибутива: имя + локализованное состояние (детали — в тултипе).</summary>
    private static string LocalizeDistroStatus(WslDistroStatus distro) => distro.State switch
    {
        GuardState.On => Loc.F("Main.Guard.Wsl.DistroOnFmt", distro.Distro, distro.ScriptPort),
        GuardState.Off => $"{distro.Distro}: {Loc.S("Main.Guard.State.Off")}",
        GuardState.Partial => $"{distro.Distro}: {Loc.S("Main.Guard.State.Partial")}",
        _ => $"{distro.Distro}: {LocalizeWslReason(distro.Reason)}",
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

        if (reason.Contains("auto", StringComparison.Ordinal))
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

    private void SetGuardDots(GuardState ban, GuardState policy, GuardState settings, GuardState wsl)
    {
        BanDot.Fill = BrushFor(ban);
        PolicyDot.Fill = BrushFor(policy);
        SettingsDot.Fill = BrushFor(settings);
        WslDot.Fill = BrushFor(wsl);
    }

    private static Brush BrushFor(GuardState state) => state switch
    {
        GuardState.On => new SolidColorBrush(Color.FromRgb(0x7C, 0xB9, 0x7C)),
        GuardState.Partial => new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x3E)),
        GuardState.Off => new SolidColorBrush(Color.FromRgb(0xD9, 0x6B, 0x6B)),
        _ => Brushes.Gray,
    };

    /// <summary>Бан и политика идут через элевейтнутый helper (UAC); живые проверки — сразу после.</summary>
    private async void OnGuardBanOn(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.BanAsync(enable: true, CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Ban.LiveFmt",
            Loc.S(result.Live.DnsBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"),
            Loc.S(result.Live.DirectBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"));
    });

    private async void OnGuardBanOff(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.BanAsync(enable: false, CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Ban.LiveFmt",
            Loc.S(result.Live.DnsBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"),
            Loc.S(result.Live.DirectBlocked ? "Main.Guard.Live.Blocked" : "Main.Guard.Live.Open"));
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

    /// <summary>Запись env (прокси + NO_PROXY) в settings.json Claude Code; бэкап рядом с файлом.</summary>
    private async void OnGuardSettingsSync(object sender, RoutedEventArgs e) => await RunGuardAsync(async () =>
    {
        var result = await _runtime.Guard.SyncSettingsAsync(CancellationToken.None);
        _viewModel.SetCheckResult(
            "Main.Guard.Settings.DoneFmt",
            result.ProxyUrl,
            result.BackupPath ?? Loc.S("Main.Guard.Backup.None"));
    });

    /// <summary>Обновление стартового скрипта WSL и settings.json дистрибутивов (не поднимает их).</summary>
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
            : Loc.F("Main.Guard.Wsl.DistroDoneFmt", d.Distro, d.ProxyUrl ?? ""));
        _viewModel.SetCheckText(string.Join("; ", lines));
    });

    private async void OnRefreshGuards(object sender, RoutedEventArgs e) => await RefreshGuardsAsync();

    private async Task RunGuardAsync(Func<Task> action)
    {
        if (_guardBusy)
        {
            return;
        }

        _guardBusy = true;
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
            _viewModel.SetGuardBusy(false);
            await RefreshGuardsAsync();
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

    /// <summary>
    /// Сторож автостопа по простою (порт ts-sidecar-watch-claude.ps1): цикл живёт, пока
    /// открыт запускший его процесс — здесь это окно приложения. Включение/выключение —
    /// явное действие пользователя; при простое сторож сам гасит WSL-хост и цепочку
    /// (подтверждение UAC на останов демона) и снимает галку.
    /// </summary>
    private void OnWatchdogToggled(object sender, RoutedEventArgs e)
    {
        if (_watchdogSyncing)
        {
            return;
        }

        if (WatchdogBox.IsChecked == true)
        {
            _watchdogCts = new CancellationTokenSource();
            _viewModel.SetCheckResult("Main.Watchdog.StartedFmt", _runtime.Config.Watchdog.CheckSeconds);
            _ = RunWatchdogAsync(_watchdogCts.Token);
        }
        else
        {
            _watchdogCts?.Cancel();
        }
    }

    private async Task RunWatchdogAsync(CancellationToken ct)
    {
        try
        {
            await _runtime.Watchdog.RunAsync(null, ct);
            _viewModel.SetCheckResult("Main.Watchdog.StoppedIdle");
        }
        catch (OperationCanceledException)
        {
            _viewModel.SetCheckResult("Main.Watchdog.StoppedManual");
        }
        catch (Exception ex)
        {
            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _watchdogCts?.Dispose();
            _watchdogCts = null;
            _watchdogSyncing = true;
            WatchdogBox.IsChecked = false;
            _watchdogSyncing = false;
            await RefreshChainAsync();
        }
    }

    /// <summary>
    /// Запуск Claude (порт claude-launcher.ps1): сначала живой CONNECT-roundtrip; если цепочка
    /// отвечает — Claude стартует сразу, без заставки. Иначе поднимается заставка подъёма и
    /// лончер идемпотентно поднимает цепочку; сбой рисуется заставкой (лог/закрыть,
    /// автозакрытие минутой), успех — «Готово» и короткое затухание.
    /// </summary>
    private async void OnLaunchClaude(object sender, RoutedEventArgs e) => await RunLaunchAsync();

    /// <summary>
    /// Ярлыковый режим (App --launch вместо ярлыка Claude): Claude уже запущен — просто
    /// показать окно, без второго экземпляра; не запущен — обычный запуск с заставкой
    /// подъёма цепочки (старт туннеля, проверки, затем Claude с флагами прокси).
    /// </summary>
    public async void LaunchFromShortcut()
    {
        if (_launchBusy)
        {
            return;
        }

        bool claudeRunning;
        try
        {
            claudeRunning = _runtime.Updater.IsClaudeRunning();
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

    private async Task RunLaunchAsync()
    {
        if (_launchBusy)
        {
            return;
        }

        _launchBusy = true;
        _viewModel.SetLaunchBusy(true);
        SplashWindow? splash = null;
        try
        {
            var healthy = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15));
            if (!healthy)
            {
                splash = new SplashWindow(_runtime.Launcher.Log.FilePath) { Owner = this };
                splash.Show();
            }

            var progress = splash is null ? null : new Progress<ChainStep>(splash.Update);
            var result = await _runtime.Launcher.LaunchAsync(LauncherOptions.Default, progress, CancellationToken.None);
            if (splash is not null)
            {
                splash.Complete(Loc.F("Splash.CompleteFmt", Math.Round(result.Elapsed.TotalSeconds, 1)));
                await Task.Delay(1000);
                splash.CloseSplash();
            }

            _viewModel.SetCheckResult("Main.Launch.DoneFmt", Math.Round(result.Elapsed.TotalSeconds, 1));
        }
        catch (Exception ex)
        {
            if (splash is not null)
            {
                splash.Fail(ex.Message, Loc.F("Splash.Fail.HintFmt", _runtime.Launcher.Log.FilePath));
                await splash.ClosedAsync; // кнопки/автозакрытие через 60 с — как Wait-SplashClose в прежней схеме
            }

            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _launchBusy = false;
            _viewModel.SetLaunchBusy(false);
            await RefreshChainAsync();
        }
    }

    /// <summary>
    /// Запуск браузера с изолированным профилем и прокси приложения (порт browser-proxy.ps1):
    /// та же схема, что у запуска Claude — живой CONNECT-roundtrip, заставка только при
    /// реальном ожидании подъёма цепочки. Уже запущенный наш экземпляр не дублируется
    /// (Firefox на том же профиле упирается в блокировку) — пометка в строке результата.
    /// </summary>
    private async void OnLaunchBrowser(object sender, RoutedEventArgs e) => await RunBrowserLaunchAsync();

    /// <summary>
    /// Ярлыковый режим (App --launch-browser вместо ярлыка «Claude (браузер)»): браузер
    /// с нашим профилем уже запущен — просто окно приложения; не запущен — обычный запуск.
    /// </summary>
    public async void LaunchBrowserFromShortcut() => await RunBrowserLaunchAsync();

    private async Task RunBrowserLaunchAsync()
    {
        if (_launchBusy)
        {
            return;
        }

        bool browserRunning;
        try
        {
            browserRunning = _runtime.Browser.IsOurBrowserRunning();
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

        _launchBusy = true;
        _viewModel.SetLaunchBusy(true);
        SplashWindow? splash = null;
        try
        {
            var healthy = await _runtime.CheckHealthAsync(TimeSpan.FromSeconds(15));
            if (!healthy)
            {
                splash = new SplashWindow(_runtime.Browser.Log.FilePath, Loc.S("Splash.Browser.Title")) { Owner = this };
                splash.Show();
            }

            var progress = splash is null ? null : new Progress<ChainStep>(splash.Update);
            var result = await _runtime.Browser.LaunchAsync(BrowserLauncherOptions.Default, progress, CancellationToken.None);
            if (splash is not null)
            {
                splash.Complete(Loc.F("Splash.CompleteFmt", Math.Round(result.Elapsed.TotalSeconds, 1)));
                await Task.Delay(1000);
                splash.CloseSplash();
            }

            _viewModel.SetCheckResult("Main.Browser.DoneFmt", Math.Round(result.Elapsed.TotalSeconds, 1));
        }
        catch (Exception ex)
        {
            if (splash is not null)
            {
                splash.Fail(ex.Message, Loc.F("Splash.Fail.HintFmt", _runtime.Browser.Log.FilePath));
                await splash.ClosedAsync;
            }

            _viewModel.SetCheckResult("Main.ErrorFmt", ex.Message);
        }
        finally
        {
            _launchBusy = false;
            _viewModel.SetLaunchBusy(false);
            await RefreshChainAsync();
        }
    }

    /// <summary>
    /// Сигнал о найденном на старте обновлении Claude: строка возле кнопок запуска.
    /// Возвращает локализованный текст — им же App показывает балун трея.
    /// </summary>
    public string SetUpdateNotice(string installedVersion, string? offeredVersion)
    {
        _viewModel.SetUpdateNotice(installedVersion, offeredVersion);
        return offeredVersion is null
            ? Loc.S("Main.Update.Available")
            : Loc.F("Main.Update.AvailableFmt", installedVersion, offeredVersion);
    }

    /// <summary>
    /// Окно обновления Claude Desktop (MSIX через прокси-цепочку): одно на приложение,
    /// повторный клик активирует открытое.
    /// </summary>
    private void OnUpdateClaude(object sender, RoutedEventArgs e)
    {
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(_runtime) { Owner = this };
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
    }

    /// <summary>
    /// Окно диагностики (порт proxy-status.ps1), только чтение: сводка состояния и просмотр
    /// логов; одно на приложение, повторный клик активирует открытое.
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
    private string? _checkResultLiteral;
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
    private bool _launchRunning;
    private string? _updateNoticeKey;
    private object?[] _updateNoticeArgs = [];

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
    public string CheckResult => _checkResultKey is null ? _checkResultLiteral ?? "" : Loc.F(_checkResultKey, _checkResultArgs);

    /// <summary>Готовый текст без локализации (составные результаты операций гардов).</summary>
    public void SetCheckText(string text)
    {
        _checkResultKey = null;
        _checkResultLiteral = text;
        OnPropertyChanged(nameof(CheckResult));
    }

    /// <summary>Сигнал о найденном на старте обновлении Claude; пуст, когда обновления нет.</summary>
    public string UpdateNotice => _updateNoticeKey is null ? "" : Loc.F(_updateNoticeKey, _updateNoticeArgs);

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

        OnPropertyChanged(nameof(UpdateNotice));
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
        private set => SetField(ref _chainSummary, value);
    }

    public string ChainDetails
    {
        get => _chainDetails;
        private set => SetField(ref _chainDetails, value);
    }

    public bool CanChainUp => !_chainOperationRunning && !_launchRunning;

    public bool CanChainDown => !_chainOperationRunning && !_launchRunning;

    public bool CanLaunch => !_chainOperationRunning && !_launchRunning;

    public bool CanGuard { get; private set; } = true;

    /// <summary>Занятость операций гардов (UAC/запись): кнопки карточки гаснут на время действия.</summary>
    public void SetGuardBusy(bool busy)
    {
        CanGuard = !busy;
        OnPropertyChanged(nameof(CanGuard));
    }

    public void SetLaunchBusy(bool busy)
    {
        _launchRunning = busy;
        if (busy)
        {
            ChainSummary = Loc.S("Main.Launch.Busy");
        }

        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanChainUp));
        OnPropertyChanged(nameof(CanChainDown));
    }

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
        OnPropertyChanged(nameof(UpdateNotice));
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
