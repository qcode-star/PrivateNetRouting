namespace PrivateNetRouting.Core.Guard;

public enum GuardKind
{
    /// <summary>Local direct-access ban: hosts + NRPT + firewall.</summary>
    LocalBan,

    /// <summary>Managed policy of Claude egressProxyUrl + disableWslSessions=0.</summary>
    EgressPolicy,

    /// <summary>env (HTTPS_PROXY/HTTP_PROXY/NO_PROXY) in settings.json of Claude Code, Windows.</summary>
    ClaudeSettings,

    /// <summary>The flag `[features] respect_system_proxy` in config.toml of Codex (desktop and CLI).</summary>
    CodexSettings,

    /// <summary>WSL: startup script with the proxy address + settings.json in the distro.</summary>
    WslConfig,

    /// <summary>WinINET system proxy.</summary>
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

    /// <summary>The NRPT rule covers exactly the configured subtrees (no rule — nothing to be stale).</summary>
    public bool NrptCurrent { get; init; } = true;

    /// <summary>The firewall rules block exactly the configured subnets (no rule — nothing to be stale).</summary>
    public bool FirewallCurrent { get; init; } = true;

    /// <summary>Some applied layer lags behind the config: "enable" again refreshes it.</summary>
    public bool Stale => (HostsEntries > 0 && !HostsCurrent) || !NrptCurrent || !FirewallCurrent;
}

/// <summary>Reading HKLM\Policies\Claude requires no rights; writing is administrator-only.</summary>
public sealed record EgressPolicyStatus(
    GuardState State,
    string? CurrentUrl,
    string ExpectedUrl,
    int? DisableWslSessions,
    string? Detail = null);

/// <summary>
/// Windows settings.json of Claude Code: the proxy env plus the maintained privacy/attribution
/// keys (ClaudeSettingsShape). Drift names the maintained values that are missing or different.
/// </summary>
public sealed record ClaudeSettingsStatus(
    GuardState State,
    string? CurrentProxy,
    string? CurrentNoProxy,
    string ExpectedProxy,
    string ExpectedNoProxy,
    bool FileMissing,
    string? Detail = null,
    IReadOnlyList<string>? Drift = null);

/// <summary>
/// The WinINET switch: Enabled is the system proxy being on at all, Ours — that the enabled server
/// is the address this installation wrote. A proxy enabled by the owner or another tool is Enabled
/// with Ours false: turning the switch off leaves it alone.
/// </summary>
public sealed record SystemProxyStatus(bool Enabled, string? Server, string? Override, string? Detail = null)
{
    public bool Ours { get; init; }

    /// <summary>A PAC script is configured (AutoConfigURL): WinINET prefers it to the manual proxy.</summary>
    public bool PacScript { get; init; }

    /// <summary>Automatic proxy detection (WPAD) is on: WinINET prefers it to the manual proxy too.</summary>
    public bool AutoDetect { get; init; }

    /// <summary>What overrides the manual proxy, in words; null — nothing does.</summary>
    public string? Overridden => (PacScript, AutoDetect) switch
    {
        (true, true) => "a PAC script (AutoConfigURL) and automatic proxy detection",
        (true, false) => "a PAC script (AutoConfigURL)",
        (false, true) => "automatic proxy detection",
        _ => null,
    };
}

/// <summary>
/// Status of the Codex settings layer: the flag is read from config.toml, the proxy env from the
/// .env of the same CODEX_HOME (the embedded core loads it itself — see CodexEnvFile). Files that
/// are absent (Codex is not installed or has never run) mean the layer is off, a flag or env that
/// is absent or stale — partial.
/// </summary>
public sealed record CodexSettingsStatus(
    GuardState State,
    bool? Enabled,
    bool FileMissing,
    bool EnvUpToDate,
    string? Detail = null);
