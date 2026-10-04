using System.Text.Json.Serialization;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>Корневой пользовательский конфиг приложения (см. config/appsettings.example.json).</summary>
public sealed class AppConfig
{
    public UiConfig Ui { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public ForwardProxyConfig ForwardProxy { get; set; } = new();

    public ChainConfig Chain { get; set; } = new();

    public LauncherConfig Launcher { get; set; } = new();

    public BrowserConfig Browser { get; set; } = new();

    public WatchdogConfig Watchdog { get; set; } = new();

    public UpdaterConfig Updater { get; set; } = new();

    public GuardConfig Guard { get; set; } = new();
}

/// <summary>Настройки интерфейса WPF-приложения. CLI всегда английский и их не читает.</summary>
public sealed class UiConfig
{
    /// <summary>Язык UI: "auto" — язык системы (фолбэк английский), иначе код ("en", "ru").</summary>
    public string Language { get; set; } = AppLanguage.Auto;

    /// <summary>
    /// Проверять на старте GUI, нет ли обновления Claude: только чтение версий и журнала
    /// AppX, без подъёма цепочки и без UAC; при наличии — строка в окне и балун трея.
    /// </summary>
    public bool CheckUpdatesOnStart { get; set; } = true;
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
/// Лончер браузера с изолированным профилем и своим прокси (порт browser-proxy.ps1):
/// Firefox — отдельный профиль с user.js (перезаписывается при каждом запуске, список
/// обхода всегда свежий), хромоподобный — флагами командной строки. Системные настройки
/// браузера не трогаются. Домены обхода общие с лончером Claude (launcher.bypassDomains).
/// </summary>
public sealed class BrowserConfig
{
    /// <summary>Какой браузер запускать: "firefox" | "chromium" (хромоподобный: Edge, Chrome, ...; незнакомое значение — firefox).</summary>
    public string Type { get; set; } = "firefox";

    [JsonIgnore]
    public BrowserKind EffectiveType => BrowserKindParser.Parse(Type);

    /// <summary>Адрес, открываемый в браузере (публичный домен Anthropic).</summary>
    public string Url { get; set; } = "https://claude.ai";

    /// <summary>Приватное окно: Firefox -private-window / Chromium --incognito.</summary>
    public bool Private { get; set; }

    /// <summary>
    /// Домены верхнего уровня, ходящие мимо прокси (как *.ru, *.su в прежней схеме):
    /// локальные сайты не должны уходить в зарубежный выход. Без точки, добавляется как *.tld.
    /// </summary>
    public List<string> DirectTlds { get; set; } = ["ru", "su", "by", "kz"];

    public BrowserFirefoxConfig Firefox { get; set; } = new();

    public BrowserChromiumConfig Chromium { get; set; } = new();
}

public sealed class BrowserFirefoxConfig
{
    /// <summary>firefox.exe; если не найден — откат на каталог Program Files (x86). Переменные окружения раскрываются.</summary>
    public string Path { get; set; } = @"%ProgramFiles%\Mozilla Firefox\firefox.exe";

    /// <summary>Каталог изолированного профиля (куки и входы живут только здесь).</summary>
    public string ProfileDir { get; set; } = @"%APPDATA%\PrivateNetRouting\browser\ff-profile";
}

public sealed class BrowserChromiumConfig
{
    /// <summary>Браузер хромоподобного семейства; дефолт — Edge, если не найден — откат на Program Files. Переменные окружения раскрываются.</summary>
    public string Path { get; set; } = @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe";

    /// <summary>Каталог изолированного профиля (--user-data-dir; обязателен — запущенный экземпляр игнорирует флаги).</summary>
    public string ProfileDir { get; set; } = @"%APPDATA%\PrivateNetRouting\browser\chromium-profile";
}

/// <summary>Семейство запускаемого браузера; незнакомое значение конфига — Firefox (как logging.level → info).</summary>
public enum BrowserKind
{
    Firefox,
    Chromium,
}

public static class BrowserKindParser
{
    public static BrowserKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "chromium" => BrowserKind.Chromium,
            _ => BrowserKind.Firefox,
        };
}

/// <summary>
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

/// <summary>
/// Гарды прямого доступа (порт set-egress-proxy.ps1, ban-ai-hosts.ps1, proxy-on/off.ps1,
/// sync-claude-noproxy.ps1 и стартового скрипта WSL): managed-политика egressProxyUrl,
/// три слоя локального бана (hosts/NRPT/firewall), env в settings.json Claude Code
/// (Windows и WSL), системный прокси WinINET. Идентификаторы правил бана по умолчанию
/// совпадают с прежней схемой — приложение подхватывает и ведёт уже стоящий бан,
/// а не создаёт параллельный (снять их можно и старыми скриптами).
/// </summary>
public sealed class GuardConfig
{
    // --- локальный бан: hosts + NRPT + firewall (порт ban-ai-hosts.ps1) ---

    /// <summary>Файл hosts; переменные окружения раскрываются.</summary>
    public string HostsFile { get; set; } = @"%SystemRoot%\System32\drivers\etc\hosts";

    /// <summary>Маркер начала/конца управляемого блока в hosts (точное совпадение строки).</summary>
    public string HostsBeginMarker { get; set; } = "# >>> ai-direct-ban (ban-ai-hosts.ps1, C:\\ovpn-script)";

    public string HostsEndMarker { get; set; } = "# <<< ai-direct-ban";

    /// <summary>Строка-комментарий внутри блока (человекочитаемая, ни на что не завязана).</summary>
    public string HostsComment { get; set; } = "# AI services (Claude/OpenAI) blocked from direct access; Claude: local ts-fwd proxy 127.0.0.1:8082 only";

    /// <summary>
    /// Точные имена в hosts (apex'ы поддеревьев плюс хосты, которые реально зовут приложения,
    /// и специфичные имена сервисов на общих CDN). Публичные домены Anthropic/OpenAI.
    /// </summary>
    public List<string> BanHosts { get; set; } =
    [
        "claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com",
        "claudemcpclient.com", "claudemcpcontent.com", "claude.dev", "clau.de",
        "api.anthropic.com", "console.anthropic.com", "statsig.anthropic.com",
        "downloads.claude.ai", "a.claude.ai", "assets.claude.ai", "assets-proxy.anthropic.com",
        "anthropic.qualtrics.com", "servd-anthropic-website.b-cdn.net",
        "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "chat.com", "sora.com",
        "chatgpt.site", "oaistatsig.com", "crixet.com",
        "ab.chatgpt.com", "api.openai.com", "auth.openai.com", "platform.openai.com", "cdn.oaistatic.com",
        "openai.qualtrics.com", "chatgpt.livekit.cloud", "openai.com.cdn.cloudflare.net",
        "openaicom.imgix.net", "openaiassets.blob.core.windows.net",
        "openaicomproductionae4b.blob.core.windows.net", "openaiapi-site.azureedge.net",
        "production-openaicom-storage.azureedge.net", "openaicom-api-bdcpf8c6d2e9atf6.z01.azurefd.net",
    ];

    /// <summary>Поддеревья для NRPT: на каждое две записи — apex и «.apex» (все поддомены).</summary>
    public List<string> BanTrees { get; set; } =
    [
        "claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com",
        "claudemcpclient.com", "claudemcpcontent.com", "claude.dev", "clau.de",
        "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "chat.com", "sora.com",
        "chatgpt.site", "oaistatsig.com", "crixet.com",
    ];

    /// <summary>DisplayName NRPT-правила — по нему правило опознаётся и снимается.</summary>
    public string NrptRuleName { get; set; } = "ai-direct-ban";

    /// <summary>Comment NRPT-правила (текст для человека, ни на что не завязан).</summary>
    public string NrptComment { get; set; } = "Claude/OpenAI: прямой доступ закрыт; Claude — только через локальный forward-прокси (ban-ai-hosts.ps1)";

    /// <summary>Подсети Anthropic для firewall outbound Block (публичные константы; общие облака по IP не банятся).</summary>
    public List<string> FirewallSubnets { get; set; } = ["160.79.104.0/21", "2607:6bc0::/48", "2607:6bc0:11::/48"];

    /// <summary>Group правил firewall — по ней правила опознаются и снимаются.</summary>
    public string FirewallGroup { get; set; } = "ai-direct-ban (ban-ai-hosts.ps1)";

    /// <summary>Пробное имя поддерева anthropic.com: в hosts его нет, до бана резолвится — чистая проверка NRPT.</summary>
    public string BanProbeName { get; set; } = "www.anthropic.com";

    // --- env в settings.json Claude Code (порт sync-claude-noproxy.ps1) ---

    /// <summary>settings.json Claude Code в Windows; переменные окружения раскрываются.</summary>
    public string ClaudeSettingsPath { get; set; } = @"%USERPROFILE%\.claude\settings.json";

    // --- WSL: стартовый скрипт + settings.json дистрибутивов (слой целиком опционален) ---

    /// <summary>Весь WSL-слой гарда: режим и список дистрибутивов с индивидуальным выбором синхронизации.</summary>
    public GuardWslConfig Wsl { get; set; } = new();

    /// <summary>Стартовый скрипт WSL, обновляющий адрес прокси (шлюз NAT) при старте дистрибутива; путь одинаков во всех дистрибутивах.</summary>
    public string WslStartupScript { get; set; } = "/etc/wsl.d/startup/10-claude-proxy.sh";

    // --- системный прокси WinINET (порт proxy-on/off.ps1) ---

    /// <summary>Дополнительные записи обхода системного прокси (локальные адреса и приватные диапазоны фиксированы в коде).</summary>
    public List<string> SystemProxyExtraBypass { get; set; } = ["*.ru", "*.su", "*.by", "*.kz"];
}

/// <summary>
/// WSL-слой гарда: полностью опционален. mode: "on" — вести всегда; "off" — не трогать вовсе;
/// "auto" — вкл, если WSL есть и хотя бы один настроенный дистрибутив установлен на машине,
/// иначе выкл. По каждому дистрибутиву из списка — свой выбор, синкать ли в него конфиг
/// (sync=false — только показывать статус, ничего не писать).
/// </summary>
public sealed class GuardWslConfig
{
    /// <summary>"on" | "off" | "auto".</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>Дистрибутивы: имя и пользователь (владелец ~/.claude) обязательны, остальное игнорируется.</summary>
    public List<WslDistroConfig> Distros { get; set; } = [];
}

public sealed class WslDistroConfig
{
    /// <summary>Имя дистрибутива, как в `wsl --list`.</summary>
    public string Name { get; set; } = "";

    /// <summary>Пользователь дистрибутива (владелец ~/.claude/settings.json).</summary>
    public string User { get; set; } = "";

    /// <summary>Синхронизировать конфиг Claude в этот дистрибутив; false — только статус.</summary>
    public bool Sync { get; set; } = true;
}

public sealed class LoggingConfig
{
    /// <summary>Каталог логов; переменные окружения (%LOCALAPPDATA% и т.п.) раскрываются при загрузке.</summary>
    public string Directory { get; set; } = @"%LOCALAPPDATA%\PrivateNetRouting\logs";

    /// <summary>
    /// Минимальный уровень записей: "debug" | "info" | "warn" | "error". По умолчанию "info" —
    /// разовые события без per-request шума; "debug" — для разбора проблем (незнакомое → info).
    /// </summary>
    public string Level { get; set; } = "info";

    /// <summary>Порог ротации; сама ротация выполняется один раз на старте процесса.</summary>
    public int MaxBytes { get; set; } = 1_000_000;

    /// <summary>Сколько старых файлов (.1, .2, ...) держать.</summary>
    public int Keep { get; set; } = 2;

    [JsonIgnore]
    public LogLevel EffectiveLevel => LogLevelParser.Parse(Level);
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
