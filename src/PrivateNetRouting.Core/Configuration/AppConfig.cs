using System.Text.Json.Serialization;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// Root user config (see config/appsettings.example.json). The shared facts (bypass zones,
/// direct TLDs, the restart grace, the PowerShell path) live at the top level; everything
/// app-specific lives in the claude/codex sections; the WSL layer has its own section.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Proxy bypass zones shared by every consumer: both apps' --proxy-bypass-list flags,
    /// NO_PROXY of the settings sync (Claude/Codex/WSL), the system proxy bypass, the browser
    /// profile. Each zone goes into the lists in two forms (zone and *.zone); the base entries
    /// (localhost, private ranges) are fixed in code.
    /// </summary>
    public List<string> BypassDomains { get; set; } = [];

    /// <summary>
    /// TLDs that never leave through the chain exit (*.tld): the browser profile bypass and
    /// the WinINET system proxy bypass are both built from this one list.
    /// </summary>
    public List<string> DirectTlds { get; set; } = [];

    /// <summary>
    /// Restart of an already running copy without our proxy flags: how long to wait for it to
    /// close after a window close request before the rest is killed; sec. Shared by the Claude
    /// and Codex launches.
    /// </summary>
    public int RestartGraceSeconds { get; set; } = 15;

    /// <summary>
    /// Closing a desktop app before an update installation: how many seconds to wait for a
    /// graceful exit before killing the processes. Shared by the Claude and Codex updates.
    /// </summary>
    public int CloseWaitSeconds { get; set; } = 25;

    /// <summary>Pause after killing the app processes before an installation, sec.</summary>
    public int KillSettleSeconds { get; set; } = 3;

    /// <summary>Windows PowerShell (AppX and NRPT/firewall cmdlets) — an OS component.</summary>
    public string PowerShellPath { get; set; } = @"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe";

    public UiConfig Ui { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public ClaudeConfig Claude { get; set; } = new();

    public CodexConfig Codex { get; set; } = new();

    public ForwardProxyConfig ForwardProxy { get; set; } = new();

    public ChainConfig Chain { get; set; } = new();

    public WslConfig Wsl { get; set; } = new();

    public WatchdogConfig Watchdog { get; set; } = new();

    public BrowserConfig Browser { get; set; } = new();

    public GuardConfig Guard { get; set; } = new();

    /// <summary>Hosts names of the local ban: the union of the Claude and Codex lists.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> BanHosts => Claude.BanHosts.Concat(Codex.BanHosts).ToArray();

    /// <summary>NRPT subtree apexes of the local ban: the union of the Claude and Codex lists.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> BanTrees => Claude.BanTrees.Concat(Codex.BanTrees).ToArray();

    /// <summary>Firewall outbound Block subnets: the union of the Claude and Codex lists.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> FirewallSubnets => Claude.FirewallSubnets.Concat(Codex.FirewallSubnets).ToArray();
}

/// <summary>WPF application UI settings. The CLI is always English and never reads them.</summary>
public sealed class UiConfig
{
    /// <summary>UI language: "auto" — system language (fallback English), otherwise a code ("en", "ru").</summary>
    public string Language { get; set; } = AppLanguage.Auto;

    /// <summary>
    /// Check at GUI start whether a Claude/Codex update is available: read-only versions and
    /// catalogs, without bringing up the chain and without UAC.
    /// </summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>
    /// One check at GUI start whether an update of the application itself is available (GitHub
    /// releases of the repository, no chain and no UAC); if there is one — an offer to open the
    /// releases page.
    /// </summary>
    public bool CheckAppUpdatesOnStart { get; set; } = true;

    /// <summary>Chain card of the main window: false — collapsed to the compact status row.</summary>
    public bool ChainCardExpanded { get; set; } = true;

    /// <summary>Guards card of the main window: false — collapsed to the compact status row.</summary>
    public bool GuardsCardExpanded { get; set; } = true;
}

/// <summary>
/// Claude Desktop: the launch/update buttons, `pnr launch`/`pnr update`, the startup update
/// check; the guard files and the ban name lists of the Claude side.
/// </summary>
public sealed class ClaudeConfig
{
    /// <summary>
    /// Whether this installation uses Claude Desktop at all: false — its launch/update buttons,
    /// its tray item and its startup update check disappear, `pnr launch`/`pnr update` refuse
    /// with a note. The guards, the chain and the idle stopper keep working as before.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Desktop app executable (MSIX execution alias); environment variables are expanded.</summary>
    public string AliasPath { get; set; } = @"%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe";

    /// <summary>Desktop app main process name — closed before an update installation.</summary>
    public string ProcessName { get; set; } = "Claude";

    /// <summary>
    /// settings.json of Claude Code on Windows: the settings sync keeps the proxy env
    /// (HTTPS_PROXY/HTTP_PROXY/NO_PROXY) there; surgical edits, .pnr-bak-* backups.
    /// </summary>
    public string SettingsPath { get; set; } = @"%USERPROFILE%\.claude\settings.json";

    /// <summary>Exact names for the hosts layer of the local ban (public Anthropic domains).</summary>
    public List<string> BanHosts { get; set; } =
    [
        "claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com",
        "claudemcpclient.com", "claudemcpcontent.com", "claude.dev", "clau.de",
        "api.anthropic.com", "console.anthropic.com", "statsig.anthropic.com",
        "downloads.claude.ai", "a.claude.ai", "assets.claude.ai", "assets-proxy.anthropic.com",
        "anthropic.qualtrics.com", "servd-anthropic-website.b-cdn.net",
    ];

    /// <summary>Subtree apexes for the NRPT layer of the local ban.</summary>
    public List<string> BanTrees { get; set; } =
    [
        "claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com",
        "claudemcpclient.com", "claudemcpcontent.com", "claude.dev", "clau.de",
    ];

    /// <summary>
    /// Anthropic subnets for the firewall outbound Block: Anthropic runs its own network, so
    /// banning by destination IP is possible.
    /// </summary>
    public List<string> FirewallSubnets { get; set; } = ["160.79.104.0/21", "2607:6bc0::/48", "2607:6bc0:11::/48"];

    public ClaudeUpdateConfig Update { get; set; } = new();
}

/// <summary>
/// Manual Claude Desktop (MSIX) update through the proxy chain: version check before the chain
/// bring-up ("nothing to install" — no UAC), package URL from Claude's own update feed or the
/// AppX journal (or manual entry), chunked download, Publisher verification, installation.
/// </summary>
public sealed class ClaudeUpdateConfig
{
    /// <summary>MSIX package name (Get-AppxPackage -Name).</summary>
    public string PackageName { get; set; } = "Claude";

    /// <summary>Package distribution host (a public constant; feed and journal URLs are filtered by it).</summary>
    public string PackageHost { get; set; } = "downloads.claude.ai";

    /// <summary>
    /// Claude's own update feed (a public constant — the same endpoint its built-in updater
    /// polls), asked through the live chain. Empty — do not ask the feed (the journal stays).
    /// </summary>
    public string UpdateFeedUrl { get; set; } = "https://api.anthropic.com/api/desktop/win32/x64/msix/update";

    /// <summary>Timeout of one update feed request, sec.</summary>
    public int FeedTimeoutSeconds { get; set; } = 15;

    /// <summary>How many recent AppX journal events to scan when looking up the package URL.</summary>
    public int JournalMaxEvents { get; set; } = 400;

    /// <summary>Cache of packages and download fragments; environment variables are expanded.</summary>
    public string CacheDir { get; set; } = @"%LOCALAPPDATA%\PrivateNetRouting\update-cache";

    /// <summary>Download chunk size.</summary>
    public int ChunkBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Chunks to download in parallel (a single stream through the chain hits DoH latency).</summary>
    public int ParallelChunks { get; set; } = 6;

    /// <summary>Retries per chunk after a failure.</summary>
    public int ChunkRetries { get; set; } = 3;

    /// <summary>Pause between chunk attempts, sec.</summary>
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>Timeout of a single chunk attempt, sec.</summary>
    public int ChunkTimeoutSeconds { get; set; } = 1800;
}

/// <summary>
/// Codex desktop app (OpenAI, a Microsoft Store MSIX): the launch/update buttons, `pnr codex
/// launch|update`. Electron/Chromium: the main process takes --proxy-server/--proxy-bypass-list
/// like Claude, its embedded CLI core takes the standard proxy env variables from CODEX_HOME/.env.
/// The launch goes through Invoke-CommandInDesktopPackage: the entry-point stub loses arguments,
/// and the package-context launch hands the process both the flags and the env.
/// </summary>
public sealed class CodexConfig
{
    /// <summary>Whether this installation uses the Codex desktop app at all (see claude.enabled).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>MSIX package name of the Codex desktop app (Get-AppxPackage -Name).</summary>
    public string PackageName { get; set; } = "OpenAI.Codex";

    /// <summary>Application id inside the package manifest (Invoke-CommandInDesktopPackage -AppId).</summary>
    public string AppId { get; set; } = "App";

    /// <summary>Main executable inside the package, relative to InstallLocation (the manifest entry point; the Codex.exe stub loses arguments).</summary>
    public string AppExecutable { get; set; } = @"app\ChatGPT.exe";

    /// <summary>
    /// Main process name of the desktop app: already-running detection, closing before an
    /// update, the watchdog activity check.
    /// </summary>
    public string ProcessName { get; set; } = "ChatGPT";

    /// <summary>
    /// config.toml of Codex (desktop and CLI) — the settings sync adds
    /// `[features] respect_system_proxy = true` there; surgical edits, .pnr-bak-* backups.
    /// </summary>
    public string SettingsPath { get; set; } = @"%USERPROFILE%\.codex\config.toml";

    /// <summary>
    /// .env of Codex in the same CODEX_HOME — the settings sync keeps the proxy env there. The
    /// embedded CLI core loads this file after its own start: the desktop app strips the
    /// inherited proxy env from the processes it spawns, so this file is what carries the proxy
    /// to the core.
    /// </summary>
    public string EnvPath { get; set; } = @"%USERPROFILE%\.codex\.env";

    /// <summary>Exact names for the hosts layer of the local ban (public OpenAI domains).</summary>
    public List<string> BanHosts { get; set; } =
    [
        "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "chat.com", "sora.com",
        "chatgpt.site", "oaistatsig.com", "crixet.com",
        "ab.chatgpt.com", "api.openai.com", "auth.openai.com", "platform.openai.com", "cdn.oaistatic.com",
        "persistent.oaistatic.com", "releases.openai.com",
        "openai.qualtrics.com", "chatgpt.livekit.cloud", "openai.com.cdn.cloudflare.net",
        "openaicom.imgix.net", "openaiassets.blob.core.windows.net",
        "openaicomproductionae4b.blob.core.windows.net", "openaiapi-site.azureedge.net",
        "production-openaicom-storage.azureedge.net", "openaicom-api-bdcpf8c6d2e9atf6.z01.azurefd.net",
    ];

    /// <summary>Subtree apexes for the NRPT layer of the local ban.</summary>
    public List<string> BanTrees { get; set; } =
    [
        "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "chat.com", "sora.com",
        "chatgpt.site", "oaistatsig.com", "crixet.com",
    ];

    /// <summary>
    /// Subnets for the firewall outbound Block: empty by default — OpenAI's endpoints ride on
    /// shared clouds (Cloudflare anycast; only egress ranges are published), so there is nothing
    /// of its own to ban by destination IP.
    /// </summary>
    public List<string> FirewallSubnets { get; set; } = [];

    public CodexUpdateConfig Update { get; set; } = new();
}

/// <summary>Codex desktop app update through the Microsoft Store (no downloads of our own, no UAC).</summary>
public sealed class CodexUpdateConfig
{
    /// <summary>Microsoft Store product id — the key the Store update check asks by (a public constant).</summary>
    public string StoreProductId { get; set; } = "9PLM9XGG6VKS";

    /// <summary>
    /// OpenAI's own version manifest of the Store package (a public constant): names the offered
    /// version only, asked through the live chain — the host is under the direct-access ban.
    /// Empty — do not ask.
    /// </summary>
    public string UpdateVersionUrl { get; set; } =
        "https://persistent.oaistatic.com/codex-app-prod/windows-store-update.json";

    /// <summary>Hard limit of one Store update (search, download and install together), sec.</summary>
    public int UpdateTimeoutSeconds { get; set; } = 1800;
}

/// <summary>
/// The userspace sidecar of the tailscale chain mode: daemon + keeper + node, their paths,
/// listeners and timeouts.
/// </summary>
public sealed class ChainTailscaleConfig
{
    public string TailscaledPath { get; set; } = @"C:\Program Files\Tailscale\tailscaled.exe";

    public string CliPath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";

    /// <summary>Daemon statedir: the node key lives here; an empty node requires a tailnet login.</summary>
    public string StateDir { get; set; } = @"%APPDATA%\PrivateNetRouting\sidecar";

    /// <summary>Daemon named pipe; the CLI and the DoH netmap discovery attach to it.</summary>
    public string Socket { get; set; } = @"\\.\pipe\pnr-sidecar";

    /// <summary>Daemon SOCKS5 listener: the forward proxy and the browser egress=chain dial it.</summary>
    public string SocksListen { get; set; } = "127.0.0.1:18083";

    /// <summary>Daemon HTTP listener: its appearance = the daemon is alive.</summary>
    public string HttpListen { get; set; } = "127.0.0.1:18082";

    /// <summary>
    /// How the daemon start/stop is elevated: "auto" (default) — directly on tailscale 1.104.1+
    /// (--windows-mode=dev, the pipe is owned by the user), through the elevated runas copy
    /// (UAC) on older builds; "never"/"always" force one of the modes ("always" — old builds
    /// only: newer daemons reject a custom socket without the dev flag even elevated).
    /// Unknown — auto.
    /// </summary>
    public string Elevation { get; set; } = "auto";

    [JsonIgnore]
    public DaemonElevationMode EffectiveElevation => DaemonElevationParser.Parse(Elevation);

    /// <summary>Tailnet coordination server; empty — taken from the node state (statedir).</summary>
    public string LoginServer { get; set; } = "";

    /// <summary>Wait for the daemon listeners after start, sec.</summary>
    public int DaemonWaitSeconds { get; set; } = 15;

    /// <summary>Pause to check the keeper after start (one that exits immediately = an error), sec.</summary>
    public int KeeperCheckSeconds { get; set; } = 2;

    /// <summary>tailscale up timeout (login by key from statedir is silent), sec.</summary>
    public int UpTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Hard limit of one tailscale CLI call (status, down): a daemon that stopped answering its
    /// pipe must not hang the whole operation. "up" is bounded by upTimeoutSeconds plus a margin.
    /// </summary>
    public int CliTimeoutSeconds { get; set; } = 20;

    /// <summary>Wait for UAC confirmation and the elevated daemon start, sec.</summary>
    public int UacWaitSeconds { get; set; } = 90;
}

/// <summary>
/// The external trusted SOCKS5 upstream of the socks5 chain mode: the target name rides the
/// CONNECT and the upstream resolves it on its side. The tailscale mode ignores the whole section.
/// </summary>
public sealed class ChainSocks5Config
{
    /// <summary>The upstream "host:port".</summary>
    public string Upstream { get; set; } = "127.0.0.1:18083";

    /// <summary>Username of the upstream (RFC 1929); both empty — no authentication.</summary>
    public string User { get; set; } = "";

    /// <summary>Password of the upstream; lives only in the user config — the example holds an empty placeholder.</summary>
    public string Password { get; set; } = "";
}

/// <summary>
/// The chain that carries the traffic. The mode picks the shape (a change applies after a
/// restart): "tailscale" — the userspace sidecar (the tailscale section), "socks5" — the
/// external upstream (the socks5 section).
/// </summary>
public sealed class ChainConfig
{
    /// <summary>"tailscale" (default) or "socks5"; unknown — tailscale.</summary>
    public string Mode { get; set; } = "tailscale";

    [JsonIgnore]
    public ChainMode EffectiveMode => ChainModeParser.Parse(Mode);

    /// <summary>Wait for full chain health (CONNECT roundtrip), sec; both modes.</summary>
    public int HealthWaitSeconds { get; set; } = 90;

    public ChainTailscaleConfig Tailscale { get; set; } = new();

    public ChainSocks5Config Socks5 { get; set; } = new();
}

/// <summary>
/// The WSL layer: the guard sync into the distros and the idle-stop WSL step; entirely optional.
/// Mode "off" wins over the sync flags. The idle stopper only touches RUNNING distros of the
/// list — no distro is booted just for a check.
/// </summary>
public sealed class WslConfig
{
    /// <summary>"on" | "off" | "auto" (auto — on if WSL exists and a configured distro is installed).</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>Sync the Claude layer (the startup script + settings.json env) into every configured distro.</summary>
    public bool SyncClaude { get; set; } = true;

    /// <summary>Sync the Codex proxy env layer (proxyEnvPath) into every configured distro.</summary>
    public bool SyncCodex { get; set; } = true;

    /// <summary>Distros the application works with: the name as in `wsl --list`, the user — the owner of ~/.claude.</summary>
    public List<WslDistroConfig> Distros { get; set; } = [];

    /// <summary>
    /// Startup script inside the distro (the same path in all of them): refreshes the proxy
    /// address (the WSL NAT gateway) and the Codex env when the distro starts.
    /// </summary>
    public string StartupScriptPath { get; set; } = "/etc/wsl.d/startup/10-claude-proxy.sh";

    /// <summary>
    /// Codex proxy env snippet inside the distro: exports the application proxy only to the
    /// shells Codex itself starts (its WSL processes carry CODEX_HOME); ordinary shells stay direct.
    /// </summary>
    public string ProxyEnvPath { get; set; } = "/etc/profile.d/20-pnr-codex-proxy.sh";
}

public sealed class WslDistroConfig
{
    /// <summary>Distro name, as in `wsl --list`.</summary>
    public string Name { get; set; } = "";

    /// <summary>Distro user (owner of ~/.claude/settings.json).</summary>
    public string User { get; set; } = "";
}

/// <summary>
/// The browser profile, created once through the application's dialog (the "?" help menu):
/// a separate profile whose traffic goes through the browser egress chosen by
/// <see cref="Egress"/>. The direct TLDs of the profile bypass come from the top-level
/// directTlds; the bypass zone domains — from the top-level bypassDomains.
/// </summary>
public sealed class BrowserConfig
{
    /// <summary>
    /// Which egress the profile points at: "forward-proxy" (the application's proxy — HTTP
    /// CONNECT, names resolved on its side over DoH; default) or "chain" (the chain's final
    /// SOCKS5: tailscale — chain.tailscale.socksListen, socks5 — chain.socks5.upstream; the
    /// browser then needs no application process). An unknown value — forward-proxy.
    /// </summary>
    public string Egress { get; set; } = "forward-proxy";

    /// <summary>Parsed egress choice; an unknown config value — ForwardProxy.</summary>
    [JsonIgnore]
    public BrowserEgressMode EgressMode => BrowserEgressModeParser.Parse(Egress);

    /// <summary>Family of the profile: "firefox" | "chrome" ("chromium" is accepted too); empty — the profile has not been created yet.</summary>
    public string Type { get; set; } = "";

    /// <summary>Browser executable of the profile; empty — looked up in the standard places at launch. Environment variables are expanded.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Profile directory (the browser's own data lives only here); empty — the profile has not been created yet.</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>Desktop shortcut created together with the profile; empty — none.</summary>
    public string ShortcutPath { get; set; } = "";

    /// <summary>Timeout of the egress reachability probe before a launch, sec.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;

    /// <summary>Family of the profile; null — not created yet (or an unknown value in the config).</summary>
    [JsonIgnore]
    public BrowserKind? Kind => BrowserKindParser.ParseOrNull(Type);

    /// <summary>The profile is ready to launch: the family is known, the executable and the profile directory are set.</summary>
    [JsonIgnore]
    public bool IsConfigured => Kind is not null
        && !string.IsNullOrWhiteSpace(ExecutablePath)
        && !string.IsNullOrWhiteSpace(ProfileDir);
}

/// <summary>Family of the profile; an unknown or empty config value — not configured (null), not a silent default.</summary>
public enum BrowserKind
{
    Firefox,
    Chrome,
}

public static class BrowserKindParser
{
    /// <summary>"firefox" | "chrome" | "chromium" → the family; anything else (including empty) — null.</summary>
    public static BrowserKind? ParseOrNull(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "firefox" => BrowserKind.Firefox,
            "chrome" or "chromium" => BrowserKind.Chrome,
            _ => null,
        };

    /// <summary>The canonical spelling for the config file.</summary>
    public static string ToConfigValue(BrowserKind kind) =>
        kind == BrowserKind.Chrome ? "chrome" : "firefox";

    /// <summary>The display name of the family (log lines and the shortcut description; the UI takes its own from the dictionaries).</summary>
    public static string DisplayName(BrowserKind kind) =>
        kind == BrowserKind.Chrome ? "Chrome" : "Firefox";
}

/// <summary>Which egress a browser profile's traffic goes through (browser.egress).</summary>
public enum BrowserEgressMode
{
    /// <summary>The application's own forward proxy (forwardProxy.listen): HTTP CONNECT, names resolved on its side over DoH.</summary>
    ForwardProxy = 0,

    /// <summary>The chain's final SOCKS5: the sidecar's listener in the tailscale mode, the external upstream in the socks5 one.</summary>
    Chain = 1,
}

public static class BrowserEgressModeParser
{
    /// <summary>Config string ("forward-proxy"/"chain") to the egress mode; unknown — ForwardProxy.</summary>
    public static BrowserEgressMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "chain" => BrowserEgressMode.Chain,
        _ => BrowserEgressMode.ForwardProxy,
    };

    /// <summary>The canonical spelling for the config file.</summary>
    public static string ToConfigValue(BrowserEgressMode mode) =>
        mode == BrowserEgressMode.Chain ? "chain" : "forward-proxy";
}

/// <summary>
/// The idle stopper starts on an explicit user action (a CLI command, the chain card toggle):
/// the GUI starts a detached worker (--watchdog-worker) that outlives the application and
/// switches itself off after the idle stop — it is never registered for autostart (rule #4).
/// </summary>
public sealed class WatchdogConfig
{
    /// <summary>
    /// Arm the stopper automatically after every successful chain raise (the window button,
    /// the Claude/browser launch, `pnr chain up`); default on. A manual toggle in the chain
    /// card and the CLI form `pnr watchdog` work regardless of this flag.
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Busyness check interval, sec; also the freshness window of the activity marker.</summary>
    public int CheckSeconds { get; set; } = 600;

    /// <summary>How many seconds to wait for the detached worker to appear in the process list after its launch.</summary>
    public int WorkerStartWaitSeconds { get; set; } = 15;

    /// <summary>How many seconds to wait for the stopper to disappear after a kill.</summary>
    public int WorkerStopWaitSeconds { get; set; } = 10;
}

/// <summary>
/// Direct-access guards: the managed policy egressProxyUrl, three layers of the local ban
/// (hosts/NRPT/firewall — the name lists live in the claude/codex sections, the rule identifiers
/// here; the application maintains a ban with these identifiers, not a parallel one), the WSL
/// layer (the wsl section) and the WinINET system proxy.
/// </summary>
public sealed class GuardConfig
{
    /// <summary>hosts file; environment variables are expanded.</summary>
    public string HostsFile { get; set; } = @"%SystemRoot%\System32\drivers\etc\hosts";

    /// <summary>Start/end marker of the managed block in hosts (exact line match).</summary>
    public string HostsBeginMarker { get; set; } = "# >>> ai-direct-ban";

    public string HostsEndMarker { get; set; } = "# <<< ai-direct-ban";

    /// <summary>Comment line inside the block (human-readable, not tied to anything).</summary>
    public string HostsComment { get; set; } = "# AI services (Claude/OpenAI) are blocked from direct access; Claude: only through the local forward proxy";

    /// <summary>DisplayName of the NRPT rule — by it the rule is identified and removed.</summary>
    public string NrptRuleName { get; set; } = "ai-direct-ban";

    /// <summary>Comment of the NRPT rule (human-readable text, not tied to anything).</summary>
    public string NrptComment { get; set; } = "Claude/OpenAI: direct access blocked; Claude — only through the local forward proxy";

    /// <summary>Group of the firewall rules — by it the rules are identified and removed.</summary>
    public string FirewallGroup { get; set; } = "ai-direct-ban";

    /// <summary>
    /// Live ban probes: each name must be inside the ban trees but absent from the hosts lists —
    /// a clean NRPT check (one per app).
    /// </summary>
    public List<string> BanProbes { get; set; } = ["www.anthropic.com", "www.openai.com"];
}

public sealed class LoggingConfig
{
    /// <summary>Log directory; environment variables (%LOCALAPPDATA% etc.) are expanded on load.</summary>
    public string Directory { get; set; } = @"%LOCALAPPDATA%\PrivateNetRouting\logs";

    /// <summary>
    /// Minimum record level: "debug" | "info" | "warn" | "error". Default "info" —
    /// one-off events without per-request noise; "debug" — for troubleshooting (unknown → info).
    /// </summary>
    public string Level { get; set; } = "info";

    /// <summary>Rotation threshold: an overgrown file shifts the age chain at start and before the next record.</summary>
    public int MaxBytes { get; set; } = 1_000_000;

    /// <summary>How many old files (.1, .2, ...) to keep.</summary>
    public int Keep { get; set; } = 2;

    [JsonIgnore]
    public LogLevel EffectiveLevel => LogLevelParser.Parse(Level);
}

/// <summary>
/// The local CONNECT proxy. It exists for two reasons. The main one is the tailscale mode: the
/// sidecar's own SOCKS5 (and HTTP) proxy resolves names with the system resolver, and the
/// direct-access ban blackholes exactly the AI names there — so this proxy does DoH itself and
/// dials by IP through the sidecar. The second one is a plain SOCKS5 upstream that does not
/// speak HTTP: applications need HTTP CONNECT, and this proxy translates.
/// </summary>
public sealed class ForwardProxyConfig
{
    /// <summary>
    /// THE switch of the local proxy. true — the proxy runs as part of the chain (the GUI does
    /// not raise it on open; chain up and the launches do), and `pnr proxy` may start it
    /// standalone. false — the local proxy does not exist at all: in the socks5 mode every
    /// client is pointed at <see cref="ExternalAppsEndpoint"/> instead (the upstream's own mixed
    /// HTTP+SOCKS inbound does the translation itself); in the tailscale mode the proxy is
    /// load-bearing — a chain bring-up refuses with an explicit error.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Main listener "host:port" (clients connect here).</summary>
    public string Listen { get; set; } = "127.0.0.1:18081";

    /// <summary>
    /// The HTTP CONNECT endpoint written for the applications when the local proxy is off
    /// (socks5 mode). Must be reachable from the WSL distros when the WSL sync is on: the
    /// upstream has to listen on the WSL-facing address then (loopback-only is unreachable from
    /// a distro, and the guard status warns about it).
    /// </summary>
    public string ExternalAppsEndpoint { get; set; } = "127.0.0.1:2080";

    /// <summary>One-time bind on the WSL NAT gateway (the adapter address is looked up at start with retries).</summary>
    public bool BindWslGateway { get; set; } = true;

    /// <summary>
    /// The proxy in a separate detached worker (its own exe with the --proxy-worker marker) that survives
    /// the exit of the command/GUI: chain up brings the worker up, chain down
    /// stops it. Disabled — the proxy lives in the GUI/command process.
    /// </summary>
    public bool Detached { get; set; }

    /// <summary>How many seconds to wait for the detached worker's listener after its launch.</summary>
    public int WorkerStartWaitSeconds { get; set; } = 15;

    /// <summary>How many seconds to wait for the port to be released after stopping the detached worker.</summary>
    public int WorkerStopWaitSeconds { get; set; } = 10;

    /// <summary>
    /// Optional extra DoH resolvers ("http(s)://IP:PORT/dns-query") on top of netmap discovery
    /// (tailscale mode only; in the socks5 mode DoH is not used at all).
    /// </summary>
    public List<string> DohSeed { get; set; } = [];

    public DohDiscoveryConfig DohDiscovery { get; set; } = new();

    /// <summary>Health-check target (CONNECT-roundtrip through our own proxy); a public Anthropic API host.</summary>
    public string HealthCheckHost { get; set; } = "api.anthropic.com";

    public int HealthCheckPort { get; set; } = 443;

    public int DohTimeoutSeconds { get; set; } = 8;

    public int DohProbeTimeoutSeconds { get; set; } = 4;

    public int SocksTimeoutSeconds { get; set; } = 12;

    /// <summary>Timeout for reading the request header from the client.</summary>
    public int HeaderTimeoutSeconds { get; set; } = 30;

    /// <summary>DoH pool refresh period (discovery + probes).</summary>
    public int DohRefreshSeconds { get; set; } = 600;

    /// <summary>Cooldown of a rejected DoH endpoint.</summary>
    public int DohDeadCooldownSeconds { get; set; } = 300;

    /// <summary>Domain for probing DoH endpoints (already routed, no side effects).</summary>
    public string DohProbeName { get; set; } = "claude.ai";

    /// <summary>Resolution cache: the response TTL is clamped to [Min, Max].</summary>
    public int CacheTtlMinSeconds { get; set; } = 60;

    public int CacheTtlMaxSeconds { get; set; } = 3600;

    /// <summary>Activity marker: the file mtime = the time of the last proxied request.</summary>
    public string ActivityMarkerPath { get; set; } = @"%APPDATA%\PrivateNetRouting\proxy-activity";

    public int ActivityTouchIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Auto-completion of the DoH pool from the tailnet config (tailscale debug netmap through the
/// sidecar pipe of chain.tailscale). On by default; in the socks5 mode DoH is not used at all.
/// </summary>
public sealed class DohDiscoveryConfig
{
    public bool Enabled { get; set; } = true;
}
