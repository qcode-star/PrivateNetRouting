using System.Text.Json.Serialization;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>Корневой пользовательский конфиг приложения (см. config/appsettings.example.json).</summary>
public sealed class AppConfig
{
    public LoggingConfig Logging { get; set; } = new();

    public ForwardProxyConfig ForwardProxy { get; set; } = new();
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
    public string Listen { get; set; } = "127.0.0.1:8082";

    /// <summary>Одноразовый бинд на шлюзе WSL NAT (адрес адаптера ищется на старте с повторами).</summary>
    public bool BindWslGateway { get; set; } = true;

    /// <summary>SOCKS5-апстрим "host:port" (userspace-sidecar), дозвон по IP после резолва.</summary>
    public string Socks5Upstream { get; set; } = "127.0.0.1:1055";

    /// <summary>Стартовый пул DoH-резолверов в форме "http://IP:PORT/dns-query".</summary>
    public List<string> DohSeed { get; set; } = [];

    public DohDiscoveryConfig DohDiscovery { get; set; } = new();

    /// <summary>Цель health-check (CONNECT-roundtrip через собственный прокси).</summary>
    public string HealthCheckHost { get; set; } = "example.com";

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

    /// <summary>Домен для пробы DoH-эндпоинтов.</summary>
    public string DohProbeName { get; set; } = "example.com";

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
