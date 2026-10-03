using System.Text.Json.Serialization;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>Корневой пользовательский конфиг приложения (см. config/appsettings.example.json).</summary>
public sealed class AppConfig
{
    public UiConfig Ui { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public ForwardProxyConfig ForwardProxy { get; set; } = new();

    public ChainConfig Chain { get; set; } = new();

    public LauncherConfig Launcher { get; set; } = new();

    public WatchdogConfig Watchdog { get; set; } = new();

    public UpdaterConfig Updater { get; set; } = new();
}

/// <summary>Настройки интерфейса WPF-приложения. CLI всегда английский и их не читает.</summary>
public sealed class UiConfig
{
    /// <summary>Язык UI: "auto" — язык системы (фолбэк английский), иначе код ("en", "ru").</summary>
    public string Language { get; set; } = AppLanguage.Auto;
}

/// <summary>
/// Запуск Claude Desktop через прокси (порт claude-launcher.ps1): проверка/подъём цепочки,
/// флаги --proxy-server/--proxy-bypass-list. Базовые записи обхода (localhost, приватные
/// диапазоны) фиксированы в коде — окружение-зависимы только домены зон.
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>Исполняемый файл Claude Desktop (execution alias MSIX); переменные окружения раскрываются.</summary>
    public string AliasPath { get; set; } = @"%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe";

    /// <summary>Зоны обхода прокси: каждая попадает в bypass-list в двух формах (zone и *.zone).</summary>
    public List<string> BypassDomains { get; set; } = [];
}

/// <summary>
/// Параметры sidecar-цепочки (userspace-tailscaled + кипер + узел). Дефолты подобраны так,
/// чтобы не конфликтовать с параллельно работающей прежней схемой (правило №3 AGENTS.md):
/// свой пайп, свои порты, свой statedir. Замещение прежней цепочки — явная правка конфига.
/// </summary>
public sealed class ChainConfig
{
    public string TailscaledPath { get; set; } = @"C:\Program Files\Tailscale\tailscaled.exe";

    public string TailscaleCliPath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";

    /// <summary>Statedir демона: там ключ узла; отдельный от системного Tailscale.</summary>
    public string StateDir { get; set; } = @"%APPDATA%\PrivateNetRouting\ts-sidecar";

    /// <summary>Именованный пайп демона (CLI цепляется к нему).</summary>
    public string Socket { get; set; } = @"\\.\pipe\pnr-sidecar";

    /// <summary>SOCKS5-слушатель демона (дозвон forward-прокси).</summary>
    public string DaemonSocksListen { get; set; } = "127.0.0.1:18083";

    /// <summary>HTTP-слушатель демона: его появление = демон жив (так же в прежней схеме).</summary>
    public string DaemonHttpListen { get; set; } = "127.0.0.1:18081";

    /// <summary>Ожидание слушателей демона после старта.</summary>
    public int DaemonWaitSeconds { get; set; } = 15;

    /// <summary>Пауза проверки кипера после старта (сразу вылетевший = ошибка).</summary>
    public int KeeperCheckSeconds { get; set; } = 2;

    /// <summary>Координационный сервер tailnet; пусто — берётся из состояния узла.</summary>
    public string LoginServer { get; set; } = "";

    /// <summary>Таймаут tailscale up (ключом из statedir вход молчаливый).</summary>
    public int UpTimeoutSeconds { get; set; } = 60;

    /// <summary>Ожидание подтверждения UAC и старта элевейтнутого демона.</summary>
    public int UacWaitSeconds { get; set; } = 90;

    /// <summary>Ожидание полного здоровья цепочки (CONNECT-roundtrip).</summary>
    public int HealthWaitSeconds { get; set; } = 90;
}

/// <summary>
/// Автостоп по простою (порт ts-sidecar-watch-claude.ps1). Сам ничего не запускает:
/// сторож стартует по явному действию пользователя (команда CLI, переключатель приложения)
/// и живёт, пока жив запустивший его процесс — в автозапуск не прописывается (правило №4).
/// </summary>
public sealed class WatchdogConfig
{
    /// <summary>Интервал проверки занятости, сек; это же окно свежести маркера активности.</summary>
    public int CheckSeconds { get; set; } = 600;

    /// <summary>Дистрибутив WSL с хостом Claude Desktop; пусто — WSL-хост при автостопе не трогается.</summary>
    public string WslDistro { get; set; } = "";
}

/// <summary>
/// Ручное обновление Claude Desktop (MSIX): установленная версия, адрес пакета из журнала
/// AppX (или вручную), многопоточная докачка фрагментами через прокси-цепочку, сверка
/// Publisher, закрытие Claude и Add-AppxPackage (порт схемы claude-desktop-update).
/// </summary>
public sealed class UpdaterConfig
{
    /// <summary>Имя MSIX-пакета Claude Desktop (Get-AppxPackage -Name).</summary>
    public string PackageName { get; set; } = "Claude";

    /// <summary>Хост раздачи пакетов (публичная константа Anthropic; адрес ищется в журнале AppX).</summary>
    public string PackageHost { get; set; } = "downloads.claude.ai";

    /// <summary>Кэш пакетов и фрагментов докачки; переменные окружения раскрываются.</summary>
    public string CacheDir { get; set; } = @"%LOCALAPPDATA%\PrivateNetRouting\update-cache";

    /// <summary>Windows PowerShell (Get-AppxPackage/Add-AppxPackage/Get-WinEvent) — компонент ОС.</summary>
    public string PowerShellPath { get; set; } = @"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>Имя процесса Claude Desktop — закрывается перед установкой.</summary>
    public string ClaudeProcessName { get; set; } = "Claude";

    /// <summary>Сколько свежих событий журнала AppX просматривать при поиске адреса.</summary>
    public int JournalMaxEvents { get; set; } = 400;

    /// <summary>Размер фрагмента докачки.</summary>
    public int ChunkBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Фрагментов качать параллельно (один поток через цепочку упирается в задержку DoH).</summary>
    public int ParallelChunks { get; set; } = 6;

    /// <summary>Повторных попыток на фрагмент после неудачи.</summary>
    public int ChunkRetries { get; set; } = 3;

    /// <summary>Пауза между попытками фрагмента, сек.</summary>
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>Таймаут одной попытки фрагмента, сек (как --max-time curl прежней схемы).</summary>
    public int ChunkTimeoutSeconds { get; set; } = 1800;

    /// <summary>Сколько секунд ждать самостоятельного закрытия Claude перед гашением процесса.</summary>
    public int CloseWaitSeconds { get; set; } = 25;

    /// <summary>Пауза после гашения процессов Claude перед установкой, сек.</summary>
    public int KillSettleSeconds { get; set; } = 3;
}

public sealed class LoggingConfig
{
    /// <summary>Каталог логов; переменные окружения (%LOCALAPPDATA% и т.п.) раскрываются при загрузке.</summary>
    public string Directory { get; set; } = @"%LOCALAPPDATA%\PrivateNetRouting\logs";

    /// <summary>Порог ротации; сама ротация выполняется один раз на старте процесса.</summary>
    public int MaxBytes { get; set; } = 1_000_000;

    /// <summary>Сколько старых файлов (.1, .2, ...) держать.</summary>
    public int Keep { get; set; } = 2;
}

public sealed class ForwardProxyConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Основной слушатель "host:port".</summary>
    public string Listen { get; set; } = "127.0.0.1:18082";

    /// <summary>Одноразовый бинд на шлюзе WSL NAT (адрес адаптера ищется на старте с повторами).</summary>
    public bool BindWslGateway { get; set; } = true;

    /// <summary>SOCKS5-апстрим "host:port" (userspace-sidecar), дозвон по IP после резолва.</summary>
    public string Socks5Upstream { get; set; } = "127.0.0.1:18083";

    /// <summary>Стартовый пул DoH-резолверов в форме "http://IP:PORT/dns-query".</summary>
    public List<string> DohSeed { get; set; } = [];

    public DohDiscoveryConfig DohDiscovery { get; set; } = new();

    /// <summary>Цель health-check (CONNECT-roundtrip через собственный прокси); публичный API-хост Anthropic.</summary>
    public string HealthCheckHost { get; set; } = "api.anthropic.com";

    public int HealthCheckPort { get; set; } = 443;

    public int DohTimeoutSeconds { get; set; } = 8;

    public int DohProbeTimeoutSeconds { get; set; } = 4;

    public int SocksTimeoutSeconds { get; set; } = 12;

    /// <summary>Таймаут чтения заголовка запроса от клиента.</summary>
    public int HeaderTimeoutSeconds { get; set; } = 30;

    /// <summary>Период обновления пула DoH (обнаружение + пробы).</summary>
    public int DohRefreshSeconds { get; set; } = 600;

    /// <summary>Кулдаун отбракованного DoH-эндпоинта.</summary>
    public int DohDeadCooldownSeconds { get; set; } = 300;

    /// <summary>Домен для пробы DoH-эндпоинтов (уже маршрутизируется, побочных эффектов нет).</summary>
    public string DohProbeName { get; set; } = "claude.ai";

    /// <summary>Кэш резолва: TTL ответа клампится в [Min, Max].</summary>
    public int CacheTtlMinSeconds { get; set; } = 60;

    public int CacheTtlMaxSeconds { get; set; } = 3600;

    /// <summary>Маркер активности: mtime файла = время последнего проксированного запроса.</summary>
    public string ActivityMarkerPath { get; set; } = @"%APPDATA%\PrivateNetRouting\proxy-activity";

    public int ActivityTouchIntervalSeconds { get; set; } = 60;

    /// <summary>Отладочная трассировка запросов (только для наладки).</summary>
    public bool Verbose { get; set; }
}

public sealed class DohDiscoveryConfig
{
    /// <summary>Автодополнение пула DoH из конфига tailnet (tailscale debug netmap).</summary>
    public bool Enabled { get; set; }

    public string CliPath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";

    /// <summary>Именованный пайп sidecar-демона.</summary>
    public string Socket { get; set; } = @"\\.\pipe\ts-sidecar";

    [JsonIgnore]
    public TimeSpan NetmapTimeout => TimeSpan.FromSeconds(40);
}
