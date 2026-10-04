namespace PrivateNetRouting.Core.Guard;

public enum GuardKind
{
    /// <summary>Локальный бан прямого доступа: hosts + NRPT + firewall (порт ban-ai-hosts.ps1).</summary>
    LocalBan,

    /// <summary>Managed-политика Claude egressProxyUrl + disableWslSessions=0 (порт set-egress-proxy.ps1).</summary>
    EgressPolicy,

    /// <summary>env (HTTPS_PROXY/HTTP_PROXY/NO_PROXY) в settings.json Claude Code, Windows (порт sync-claude-noproxy.ps1).</summary>
    ClaudeSettings,

    /// <summary>WSL: стартовый скрипт с адресом прокси + settings.json в дистрибутиве.</summary>
    WslConfig,

    /// <summary>Системный прокси WinINET (порт proxy-on/off.ps1).</summary>
    SystemProxy,
}

public enum GuardState
{
    On,
    Off,
    Partial,
    Unknown,
}

/// <summary>
/// Статус одного гарда: только чтение, без прав администратора. Detail — готовая строка
/// для CLI (английская); GUI форматирует свои строки из типизированных статусов ниже.
/// </summary>
public sealed record GuardItemStatus(GuardKind Kind, GuardState State, string Detail);

public sealed record LocalBanStatus(
    GuardState State,
    int HostsEntries,
    bool HostsCurrent,
    int NrptRules,
    IReadOnlyList<string> NrptNamespaces,
    int FirewallRules,
    IReadOnlyList<string> FirewallSubnets,
    string? Detail = null)
{
    public static readonly LocalBanStatus Off = new(GuardState.Off, 0, false, 0, [], 0, []);
}

/// <summary>Чтение HKLM\Policies\Claude прав не требует; запись — только администратор.</summary>
public sealed record EgressPolicyStatus(
    GuardState State,
    string? CurrentUrl,
    string ExpectedUrl,
    int? DisableWslSessions,
    string? Detail = null);

public sealed record ClaudeSettingsStatus(
    GuardState State,
    string? CurrentProxy,
    string? CurrentNoProxy,
    string ExpectedProxy,
    string ExpectedNoProxy,
    bool FileMissing,
    string? Detail = null);

public sealed record SystemProxyStatus(bool Enabled, string? Server, string? Override);
