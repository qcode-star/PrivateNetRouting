namespace PrivateNetRouting.Core.Guard;

public enum GuardKind
{
    /// <summary>Local direct-access ban: hosts + NRPT + firewall (port of ban-ai-hosts.ps1).</summary>
    LocalBan,

    /// <summary>Managed policy of Claude egressProxyUrl + disableWslSessions=0 (port of set-egress-proxy.ps1).</summary>
    EgressPolicy,

    /// <summary>env (HTTPS_PROXY/HTTP_PROXY/NO_PROXY) in settings.json of Claude Code, Windows (port of sync-claude-noproxy.ps1).</summary>
    ClaudeSettings,

    /// <summary>WSL: startup script with the proxy address + settings.json in the distro.</summary>
    WslConfig,

    /// <summary>WinINET system proxy (port of proxy-on/off.ps1).</summary>
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
/// Status of one guard: read-only, no administrator rights. Detail is a ready-made string
/// for the CLI (English); the GUI formats its own strings from the typed statuses below.
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

/// <summary>Reading HKLM\Policies\Claude requires no rights; writing is administrator-only.</summary>
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
