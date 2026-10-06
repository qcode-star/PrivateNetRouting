using System.Text.Json.Serialization;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>Root user config of the application (see config/appsettings.example.json).</summary>
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

/// <summary>WPF application UI settings. The CLI is always English and never reads them.</summary>
public sealed class UiConfig
{
    /// <summary>UI language: "auto" — system language (fallback English), otherwise a code ("en", "ru").</summary>
    public string Language { get; set; } = AppLanguage.Auto;

    /// <summary>
    /// Check at GUI start whether a Claude update is available: read-only versions and the AppX
    /// journal, without bringing up the chain and without UAC; if present — a line in the window and a tray balloon.
    /// </summary>
    public bool CheckUpdatesOnStart { get; set; } = true;
}

/// <summary>
/// Launching Claude Desktop through the proxy (port of claude-launcher.ps1): chain check/bring-up,
/// the --proxy-server/--proxy-bypass-list flags. Base bypass entries (localhost, private
/// ranges) are fixed in code — only the zone domains are environment-dependent.
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>Claude Desktop executable (MSIX execution alias); environment variables are expanded.</summary>
    public string AliasPath { get; set; } = @"%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe";

    /// <summary>Proxy bypass zones: each goes into the bypass-list in two forms (zone and *.zone).</summary>
    public List<string> BypassDomains { get; set; } = [];
}

/// <summary>
/// Sidecar chain parameters (userspace-tailscaled + keeper + node). The defaults are chosen so as
/// not to conflict with the legacy scheme running in parallel (rule #3 of AGENTS.md):
/// own pipe, own ports, own statedir. Replacing the legacy chain is an explicit config edit.
/// </summary>
public sealed class ChainConfig
{
    public string TailscaledPath { get; set; } = @"C:\Program Files\Tailscale\tailscaled.exe";

    public string TailscaleCliPath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";

    /// <summary>Daemon statedir: the node key is there; separate from the system Tailscale.</summary>
    public string StateDir { get; set; } = @"%APPDATA%\PrivateNetRouting\ts-sidecar";

    /// <summary>Daemon named pipe (the CLI attaches to it).</summary>
    public string Socket { get; set; } = @"\\.\pipe\pnr-sidecar";

    /// <summary>Daemon SOCKS5 listener (the forward proxy dials it).</summary>
    public string DaemonSocksListen { get; set; } = "127.0.0.1:18083";

    /// <summary>Daemon HTTP listener: its appearance = the daemon is alive (same as in the legacy scheme).</summary>
    public string DaemonHttpListen { get; set; } = "127.0.0.1:18081";

    /// <summary>Wait for the daemon listeners after start.</summary>
    public int DaemonWaitSeconds { get; set; } = 15;

    /// <summary>Pause to check the keeper after start (one that exits immediately = an error).</summary>
    public int KeeperCheckSeconds { get; set; } = 2;

    /// <summary>Tailnet coordination server; empty — taken from the node state.</summary>
    public string LoginServer { get; set; } = "";

    /// <summary>tailscale up timeout (login by key from statedir is silent).</summary>
    public int UpTimeoutSeconds { get; set; } = 60;

    /// <summary>Wait for UAC confirmation and the elevated daemon start.</summary>
    public int UacWaitSeconds { get; set; } = 90;

    /// <summary>Wait for full chain health (CONNECT-roundtrip).</summary>
    public int HealthWaitSeconds { get; set; } = 90;
}

/// <summary>
/// Browser launcher with an isolated profile and its own proxy (port of browser-proxy.ps1):
/// Firefox — a separate profile with user.js (rewritten on every launch, the bypass
/// list is always fresh), Chromium-like — via command-line flags. The browser's system
/// settings are not touched. Bypass domains are shared with the Claude launcher (launcher.bypassDomains).
/// </summary>
public sealed class BrowserConfig
{
    /// <summary>Which browser to launch: "firefox" | "chromium" (Chromium-like: Edge, Chrome, ...; unknown value — firefox).</summary>
    public string Type { get; set; } = "firefox";

    [JsonIgnore]
    public BrowserKind EffectiveType => BrowserKindParser.Parse(Type);

    /// <summary>URL opened in the browser (a public Anthropic domain).</summary>
    public string Url { get; set; } = "https://claude.ai";

    /// <summary>Private window: Firefox -private-window / Chromium --incognito.</summary>
    public bool Private { get; set; }

    /// <summary>
    /// Top-level domains that go around the proxy (like *.ru, *.su in the legacy scheme):
    /// local sites must not go out to a foreign exit. Without the dot, added as *.tld.
    /// </summary>
    public List<string> DirectTlds { get; set; } = ["ru", "su", "by", "kz"];

    public BrowserFirefoxConfig Firefox { get; set; } = new();

    public BrowserChromiumConfig Chromium { get; set; } = new();
}

public sealed class BrowserFirefoxConfig
{
    /// <summary>firefox.exe; if not found — fallback to the Program Files (x86) directory. Environment variables are expanded.</summary>
    public string Path { get; set; } = @"%ProgramFiles%\Mozilla Firefox\firefox.exe";

    /// <summary>Isolated profile directory (cookies and logins live only here).</summary>
    public string ProfileDir { get; set; } = @"%APPDATA%\PrivateNetRouting\browser\ff-profile";
}

public sealed class BrowserChromiumConfig
{
    /// <summary>Browser of the Chromium-like family; default — Edge, if not found — fallback to Program Files. Environment variables are expanded.</summary>
    public string Path { get; set; } = @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe";

    /// <summary>Isolated profile directory (--user-data-dir; required — a running instance ignores the flags).</summary>
    public string ProfileDir { get; set; } = @"%APPDATA%\PrivateNetRouting\browser\chromium-profile";
}

/// <summary>Family of the launched browser; an unknown config value — Firefox (like logging.level → info).</summary>
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
/// The watchdog starts on an explicit user action (a CLI command, an application toggle)
/// and lives as long as the process that started it — it is not written to autostart (rule #4).
/// </summary>
public sealed class WatchdogConfig
{
    /// <summary>Busyness check interval, sec; also the freshness window of the activity marker.</summary>
    public int CheckSeconds { get; set; } = 600;

    /// <summary>WSL distro with the Claude Desktop host; empty — the WSL host is not touched on idle auto-stop.</summary>
    public string WslDistro { get; set; } = "";
}

/// <summary>
/// Manual Claude Desktop update (MSIX): installed version, package URL from the AppX journal
/// (or manual), multithreaded chunked download through the proxy chain, Publisher
/// verification, closing Claude and Add-AppxPackage (port of the claude-desktop-update scheme).
/// </summary>
public sealed class UpdaterConfig
{
    /// <summary>MSIX package name of Claude Desktop (Get-AppxPackage -Name).</summary>
    public string PackageName { get; set; } = "Claude";

    /// <summary>Package distribution host (a public Anthropic constant; the URL is looked up in the AppX journal).</summary>
    public string PackageHost { get; set; } = "downloads.claude.ai";

    /// <summary>Cache of packages and download chunks; environment variables are expanded.</summary>
    public string CacheDir { get; set; } = @"%LOCALAPPDATA%\PrivateNetRouting\update-cache";

    /// <summary>Windows PowerShell (Get-AppxPackage/Add-AppxPackage/Get-WinEvent) — an OS component.</summary>
    public string PowerShellPath { get; set; } = @"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>Claude Desktop process name — closed before installation.</summary>
    public string ClaudeProcessName { get; set; } = "Claude";

    /// <summary>How many recent AppX journal events to scan when looking up the URL.</summary>
    public int JournalMaxEvents { get; set; } = 400;

    /// <summary>Download chunk size.</summary>
    public int ChunkBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Chunks to download in parallel (a single stream through the chain hits DoH latency).</summary>
    public int ParallelChunks { get; set; } = 6;

    /// <summary>Retries per chunk after a failure.</summary>
    public int ChunkRetries { get; set; } = 3;

    /// <summary>Pause between chunk attempts, sec.</summary>
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>Timeout of a single chunk attempt, sec (like curl --max-time in the legacy scheme).</summary>
    public int ChunkTimeoutSeconds { get; set; } = 1800;

    /// <summary>How many seconds to wait for Claude to close on its own before killing the process.</summary>
    public int CloseWaitSeconds { get; set; } = 25;

    /// <summary>Pause after killing the Claude processes before installation, sec.</summary>
    public int KillSettleSeconds { get; set; } = 3;
}

/// <summary>
/// Direct-access guards (port of set-egress-proxy.ps1, ban-ai-hosts.ps1, proxy-on/off.ps1,
/// sync-claude-noproxy.ps1 and the WSL startup script): the managed policy egressProxyUrl,
/// three layers of the local ban (hosts/NRPT/firewall), env in Claude Code settings.json
/// (Windows and WSL), the WinINET system proxy. The default ban rule identifiers
/// match the legacy scheme — the application picks up and maintains the ban already in place
/// rather than creating a parallel one (they can also be removed by the old scripts).
/// </summary>
public sealed class GuardConfig
{
    // --- local ban: hosts + NRPT + firewall (port of ban-ai-hosts.ps1) ---

    /// <summary>hosts file; environment variables are expanded.</summary>
    public string HostsFile { get; set; } = @"%SystemRoot%\System32\drivers\etc\hosts";

    /// <summary>Start/end marker of the managed block in hosts (exact line match).</summary>
    public string HostsBeginMarker { get; set; } = "# >>> ai-direct-ban (ban-ai-hosts.ps1, C:\\ovpn-script)";

    public string HostsEndMarker { get; set; } = "# <<< ai-direct-ban";

    /// <summary>Comment line inside the block (human-readable, not tied to anything).</summary>
    public string HostsComment { get; set; } = "# AI services (Claude/OpenAI) blocked from direct access; Claude: local ts-fwd proxy 127.0.0.1:8082 only";

    /// <summary>
    /// Exact names in hosts (subtree apexes plus hosts that applications actually call,
    /// and service-specific names on shared CDNs). Public Anthropic/OpenAI domains.
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

    /// <summary>Subtrees for NRPT: two entries each — the apex and ".apex" (all subdomains).</summary>
    public List<string> BanTrees { get; set; } =
    [
        "claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com",
        "claudemcpclient.com", "claudemcpcontent.com", "claude.dev", "clau.de",
        "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "chat.com", "sora.com",
        "chatgpt.site", "oaistatsig.com", "crixet.com",
    ];

    /// <summary>DisplayName of the NRPT rule — by it the rule is identified and removed.</summary>
    public string NrptRuleName { get; set; } = "ai-direct-ban";

    /// <summary>Comment of the NRPT rule (human-readable text, not tied to anything).</summary>
    public string NrptComment { get; set; } = "Claude/OpenAI: direct access blocked; Claude — only through the local forward proxy (ban-ai-hosts.ps1)";

    /// <summary>Anthropic subnets for firewall outbound Block (public constants; shared clouds are not banned by IP).</summary>
    public List<string> FirewallSubnets { get; set; } = ["160.79.104.0/21", "2607:6bc0::/48", "2607:6bc0:11::/48"];

    /// <summary>Group of the firewall rules — by it the rules are identified and removed.</summary>
    public string FirewallGroup { get; set; } = "ai-direct-ban (ban-ai-hosts.ps1)";

    /// <summary>Probe name of the anthropic.com subtree: absent from hosts, resolves before the ban — a clean NRPT check.</summary>
    public string BanProbeName { get; set; } = "www.anthropic.com";

    // --- env in Claude Code settings.json (port of sync-claude-noproxy.ps1) ---

    /// <summary>Claude Code settings.json on Windows; environment variables are expanded.</summary>
    public string ClaudeSettingsPath { get; set; } = @"%USERPROFILE%\.claude\settings.json";

    // --- WSL: startup script + settings.json of the distros (the whole layer is optional) ---

    /// <summary>The whole WSL guard layer: mode and list of distros with an individual sync choice.</summary>
    public GuardWslConfig Wsl { get; set; } = new();

    /// <summary>WSL startup script that refreshes the proxy address (NAT gateway) when the distro starts; the path is the same in all distros.</summary>
    public string WslStartupScript { get; set; } = "/etc/wsl.d/startup/10-claude-proxy.sh";

    // --- WinINET system proxy (port of proxy-on/off.ps1) ---

    /// <summary>Additional system proxy bypass entries (local addresses and private ranges are fixed in code).</summary>
    public List<string> SystemProxyExtraBypass { get; set; } = ["*.ru", "*.su", "*.by", "*.kz"];
}

/// <summary>
/// WSL guard layer: fully optional. mode: "on" — always maintain; "off" — do not touch at all;
/// "auto" — on if WSL is present and at least one configured distro is installed on the machine,
/// otherwise off. For each distro in the list — its own choice of whether to sync the config
/// into it (sync=false — only show status, write nothing).
/// </summary>
public sealed class GuardWslConfig
{
    /// <summary>"on" | "off" | "auto".</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>Distros: the name and user (owner of ~/.claude) are required, the rest is ignored.</summary>
    public List<WslDistroConfig> Distros { get; set; } = [];
}

public sealed class WslDistroConfig
{
    /// <summary>Distro name, as in `wsl --list`.</summary>
    public string Name { get; set; } = "";

    /// <summary>Distro user (owner of ~/.claude/settings.json).</summary>
    public string User { get; set; } = "";

    /// <summary>Sync the Claude config into this distro; false — status only.</summary>
    public bool Sync { get; set; } = true;
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

    /// <summary>Rotation threshold; the rotation itself is performed once at process start.</summary>
    public int MaxBytes { get; set; } = 1_000_000;

    /// <summary>How many old files (.1, .2, ...) to keep.</summary>
    public int Keep { get; set; } = 2;

    [JsonIgnore]
    public LogLevel EffectiveLevel => LogLevelParser.Parse(Level);
}

public sealed class ForwardProxyConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Main listener "host:port".</summary>
    public string Listen { get; set; } = "127.0.0.1:18082";

    /// <summary>One-time bind on the WSL NAT gateway (the adapter address is looked up at start with retries).</summary>
    public bool BindWslGateway { get; set; } = true;

    /// <summary>
    /// The proxy in a separate detached worker (its own exe with the --proxy-worker marker) that survives
    /// the exit of the command/GUI (parity with ts-fwd of the legacy scheme): chain up brings the worker up, chain down
    /// stops it. Disabled — the proxy lives in the GUI/command process, as before.
    /// </summary>
    public bool Detached { get; set; }

    /// <summary>How many seconds to wait for the detached worker's listener after its launch.</summary>
    public int WorkerStartWaitSeconds { get; set; } = 15;

    /// <summary>How many seconds to wait for the port to be released after stopping the detached worker.</summary>
    public int WorkerStopWaitSeconds { get; set; } = 10;

    /// <summary>SOCKS5 upstream "host:port" (userspace-sidecar), dialing by IP after resolution.</summary>
    public string Socks5Upstream { get; set; } = "127.0.0.1:18083";

    /// <summary>Seed pool of DoH resolvers in the form "http://IP:PORT/dns-query".</summary>
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

public sealed class DohDiscoveryConfig
{
    /// <summary>Auto-completion of the DoH pool from the tailnet config (tailscale debug netmap).</summary>
    public bool Enabled { get; set; }

    public string CliPath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";

    /// <summary>Named pipe of the sidecar daemon.</summary>
    public string Socket { get; set; } = @"\\.\pipe\ts-sidecar";

    [JsonIgnore]
    public TimeSpan NetmapTimeout => TimeSpan.FromSeconds(40);
}
