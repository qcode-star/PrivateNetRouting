using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Guard;

/// <summary>Live ban checks: read-only, requires no rights.</summary>
public sealed record BanLiveChecks(
    bool DnsBlocked,
    string DnsDetail,
    bool DirectBlocked,
    string DirectDetail,
    bool? ProxiedAlive,
    string ProxiedDetail);

public sealed record BanApplyResult(string Detail, BanLiveChecks Live);

public sealed record SettingsSyncResult(
    bool Written,
    string? BackupPath,
    string ProxyUrl,
    string NoProxy,
    bool CodexWritten,
    string? CodexBackupPath,
    string CodexPath);

public sealed record GuardSnapshot(
    LocalBanStatus Ban,
    EgressPolicyStatus Policy,
    ClaudeSettingsStatus Settings,
    CodexSettingsStatus CodexSettings,
    WslConfigStatus Wsl,
    SystemProxyStatus SystemProxy,
    IReadOnlyList<GuardItemStatus> Items)
{
    public GuardItemStatus this[GuardKind kind] => Items.First(item => item.Kind == kind);
}

/// <summary>
/// Orchestration of direct-access guards: status is read-only (registry, hosts, NRPT/firewall
/// via cmdlets, settings.json, config.toml of Codex, WSL), enabling the ban/policy goes through
/// the elevated helper, the settings/WSL/system proxy need no elevation. One settings sync covers
/// both applications at once (Claude Code env and the Codex system-proxy flag). After applying the
/// ban, live checks run (rule #6 of AGENTS.md: not "it should work", but a check on live traffic).
/// </summary>
public sealed class GuardService(
    AppConfig config,
    IEgressPolicyStore policy,
    IBanSystemOps banOps,
    ClaudeSettingsGuard settings,
    CodexSettingsGuard codexSettings,
    WslConfigGuard wsl,
    ISystemProxyStore systemProxy,
    IElevatedCommandRunner elevated,
    FileLog log,
    Func<CancellationToken, Task<bool>> chainHealth)
{
    public async Task<GuardSnapshot> GetStatusAsync(CancellationToken ct)
    {
        var (ban, policyStatus) = await GetCoreStatusAsync(ct);

        var settingsStatus = settings.Read();
        var codexStatus = codexSettings.Read();
        var wslStatus = await wsl.ReadAsync(ct);
        var sysProxyStatus = systemProxy.Read();

        return new GuardSnapshot(
            ban,
            policyStatus,
            settingsStatus,
            codexStatus,
            wslStatus,
            sysProxyStatus,
            BuildItems(ban, policyStatus, settingsStatus, codexStatus, wslStatus, sysProxyStatus));
    }

    /// <summary>
    /// Ban and policy only (registry, hosts, NRPT/firewall) — the fast core for the diagnostics
    /// summary; settings.json and WSL are not read (wsl.exe takes seconds).
    /// </summary>
    public async Task<(LocalBanStatus Ban, EgressPolicyStatus Policy)> GetCoreStatusAsync(CancellationToken ct)
    {
        var guard = config.Guard;

        LocalBanStatus ban;
        try
        {
            var hosts = HostsFileBlock.Read(guard.HostsFile, guard.HostsBeginMarker, guard.HostsEndMarker, guard.HostsComment, guard.BanHosts);
            var layers = await banOps.GetLayersStateAsync(ct);
            var hostsLayerOn = hosts.Present;
            var state = hostsLayerOn && layers.NrptRules > 0 && layers.FirewallRules > 0 ? GuardState.On
                : hostsLayerOn || layers.NrptRules > 0 || layers.FirewallRules > 0 ? GuardState.Partial
                : GuardState.Off;
            ban = new LocalBanStatus(
                state,
                hosts.EntryCount,
                hosts.Current,
                layers.NrptRules,
                layers.NrptNamespaces,
                layers.FirewallRules,
                layers.FirewallSubnets);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            ban = UnknownBan(ex);
        }

        EgressPolicyStatus policyStatus;
        try
        {
            var expected = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy);
            var values = policy.Read();
            policyStatus = values.EgressProxyUrl is null
                ? new EgressPolicyStatus(GuardState.Off, null, expected, values.DisableWslSessions)
                : values.EgressProxyUrl == expected && values.DisableWslSessions == 0
                    ? new EgressPolicyStatus(GuardState.On, values.EgressProxyUrl, expected, values.DisableWslSessions)
                    : new EgressPolicyStatus(GuardState.Partial, values.EgressProxyUrl, expected, values.DisableWslSessions);
        }
        catch (Exception ex)
        {
            policyStatus = UnknownPolicy(ex);
        }

        return (ban, policyStatus);
    }

    private static LocalBanStatus UnknownBan(Exception ex) =>
        new LocalBanStatus(GuardState.Unknown, 0, false, 0, [], 0, []) { Detail = ex.Message };

    private static EgressPolicyStatus UnknownPolicy(Exception ex) =>
        new EgressPolicyStatus(GuardState.Unknown, null, "", null) { Detail = ex.Message };

    /// <summary>Ready-made English status strings for the CLI; the GUI formats from the typed fields.</summary>
    internal static IReadOnlyList<GuardItemStatus> BuildItems(
        LocalBanStatus ban,
        EgressPolicyStatus policy,
        ClaudeSettingsStatus settings,
        CodexSettingsStatus codex,
        WslConfigStatus wsl,
        SystemProxyStatus sysProxy)
    {
        return
        [
            new(GuardKind.LocalBan, ban.State, ban.Detail ?? $"hosts: {ban.HostsEntries} names{(ban.HostsCurrent || ban.HostsEntries == 0 ? "" : " (stale list)")}; NRPT: {ban.NrptRules} rule(s) on {ban.NrptNamespaces.Count} namespaces; firewall: {ban.FirewallRules} rule(s) on {ban.FirewallSubnets.Count} subnets"),
            new(GuardKind.EgressPolicy, policy.State, policy.Detail ?? (policy.State == GuardState.On
                ? $"egressProxyUrl={policy.CurrentUrl}, disableWslSessions={policy.DisableWslSessions}"
                : policy.State == GuardState.Off
                    ? "policy is not set"
                    : $"egressProxyUrl={policy.CurrentUrl} (expected {policy.ExpectedUrl})")),
            new(GuardKind.ClaudeSettings, settings.State, settings.FileMissing
                ? "settings.json not found"
                : settings.Detail ?? (settings.State == GuardState.On
                    ? $"env proxy {settings.ExpectedProxy}, NO_PROXY up to date"
                    : $"HTTPS_PROXY={settings.CurrentProxy ?? "<none>"}, NO_PROXY={settings.CurrentNoProxy ?? "<none>"} (expected {settings.ExpectedProxy})")),
            new(GuardKind.CodexSettings, codex.State, codex.Detail ?? (codex.FileMissing
                ? "config.toml not found"
                : codex.State == GuardState.On
                    ? "respect_system_proxy = true"
                    : $"respect_system_proxy={Word(codex.Enabled)} (expected true)")),
            new(GuardKind.WslConfig, wsl.State, wsl.Distros.Count == 0
                ? wsl.Reason
                : string.Join("; ", wsl.Distros.Select(d =>
                    $"{d.Distro}: {Word(d.State)}{(d.Sync ? "" : " (sync off)")} (claude script {(d.ScriptPort?.ToString() ?? "-")}, codex proxy env {(d.ProxyEnvPresent ? d.ProxyEnvPort?.ToString() ?? "present" : "missing")}){(d.Reason.Length > 0 ? $" - {d.Reason}" : "")}"))),
            new(GuardKind.SystemProxy, sysProxy.Enabled ? GuardState.On : GuardState.Off,
                sysProxy.Enabled ? $"enabled: {sysProxy.Server}" : "disabled"),
        ];
    }

    private static string Word(bool? value) => value switch
    {
        true => "true",
        false => "false",
        _ => "<unset>",
    };


    internal static string Word(GuardState state) => state switch
    {
        GuardState.On => "on",
        GuardState.Off => "off",
        GuardState.Partial => "partial",
        _ => "unknown",
    };

    /// <summary>Enabling/disabling the three ban layers (UAC) with live checks after applying.</summary>
    public async Task<BanApplyResult> BanAsync(bool enable, CancellationToken ct)
    {
        var command = enable ? GuardElevatedCommands.BanOn : GuardElevatedCommands.BanOff;
        var result = await elevated.RunAsync(command, ct);
        if (!result.Ok)
        {
            log.Error($"guard ban {(enable ? "on" : "off")}: {result.Detail}");
            throw new IOException(result.Detail);
        }

        log.Info($"guard ban {(enable ? "on" : "off")}: {result.Detail}");
        var live = await LiveChecksAsync(ct);
        return new BanApplyResult(result.Detail, live);
    }

    /// <summary>Enabling/disabling the managed policy (UAC); a control read follows.</summary>
    public async Task<string> PolicyAsync(bool enable, CancellationToken ct)
    {
        var command = enable ? GuardElevatedCommands.PolicyOn : GuardElevatedCommands.PolicyOff;
        var result = await elevated.RunAsync(command, ct);
        if (!result.Ok)
        {
            log.Error($"guard policy {(enable ? "on" : "off")}: {result.Detail}");
            throw new IOException(result.Detail);
        }

        log.Info($"guard policy {(enable ? "on" : "off")}: {result.Detail}");
        return result.Detail;
    }

    /// <summary>
    /// Ban and policy in one elevation (the combined action of the collapsed card); live checks
    /// follow, as after a plain ban. settings.json and WSL stay with the caller — they need no
    /// elevation.
    /// </summary>
    public async Task<BanApplyResult> AllAsync(bool enable, CancellationToken ct)
    {
        var command = enable ? GuardElevatedCommands.AllOn : GuardElevatedCommands.AllOff;
        var result = await elevated.RunAsync(command, ct);
        if (!result.Ok)
        {
            log.Error($"guard all {(enable ? "on" : "off")}: {result.Detail}");
            throw new IOException(result.Detail);
        }

        log.Info($"guard all {(enable ? "on" : "off")}: {result.Detail}");
        var live = await LiveChecksAsync(ct);
        return new BanApplyResult(result.Detail, live);
    }

    /// <summary>
    /// Both applications at once: env in Claude Code settings.json and the system-proxy flag in
    /// Codex config.toml (the guard card's single sync action). Each file is written only on an
    /// actual change; the log carries one line per application.
    /// </summary>
    public Task<SettingsSyncResult> SyncSettingsAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            var sync = settings.Sync();
            var codexSync = codexSettings.Sync();
            var outcome = new SettingsSyncResult(
                sync.Written,
                sync.BackupPath,
                ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy),
                ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains),
                codexSync.Written,
                codexSync.BackupPath,
                config.Guard.CodexSettingsPath);
            log.Info(outcome.Written
                ? $"guard settings sync: Claude env proxy {outcome.ProxyUrl}, NO_PROXY written (backup: {outcome.BackupPath ?? "none"})"
                : $"guard settings sync: Claude env already up to date (proxy {outcome.ProxyUrl})");
            log.Info(outcome.CodexWritten
                ? $"guard codex settings sync: respect_system_proxy=true written to config.toml (backup: {outcome.CodexBackupPath ?? "none"})"
                : "guard codex settings sync: respect_system_proxy=true already in place");
            return outcome;
        }, ct);

    public async Task<WslSyncResult> SyncWslAsync(CancellationToken ct)
    {
        var result = await wsl.SyncAsync(ct);
        if (result.LayerDisabled)
        {
            log.Warn($"guard wsl sync skipped: {result.Reason}");
        }
        else
        {
            foreach (var distro in result.Distros)
            {
                if (distro.Skipped)
                {
                    log.Info($"guard wsl sync: {distro.Distro} skipped ({distro.Reason})");
                    continue;
                }

                var what = (distro.SettingsWritten, distro.ScriptWritten) switch
                {
                    (true, true) => "settings+script written",
                    (true, false) => "settings written, script already up to date",
                    (false, true) => "settings already up to date, script written",
                    _ => "settings+script already up to date",
                };
                var backups = new[] { distro.SettingsBackup, distro.ScriptBackup }
                    .Where(b => b is not null)
                    .ToList();
                log.Info($"guard wsl sync: {distro.Distro} proxy {distro.ProxyUrl}, {what}" +
                         (backups.Count > 0 ? $" (backup: {string.Join(", ", backups)})" : ""));
            }
        }

        return result;
    }

    /// <summary>WinINET system proxy (requires no rights); disabling restores direct access.</summary>
    public Task SetSystemProxyAsync(bool enable, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (enable)
            {
                var endpoint = Net.Socks.Socks5Client.ParseEndPoint(config.ForwardProxy.Listen);
                var bypass = SystemProxyBypass.Build(config.Launcher.BypassDomains, config.Guard.SystemProxyExtraBypass);
                systemProxy.Enable($"{endpoint.Address}:{endpoint.Port}", bypass);
                log.Info($"guard sysproxy on: {endpoint.Address}:{endpoint.Port} (bypass {config.Launcher.BypassDomains.Count} domains + extras)");
            }
            else
            {
                systemProxy.Disable();
                log.Info("guard sysproxy off");
            }
        }, ct);

    /// <summary>
    /// Live checks: the probe name must not resolve and must not
    /// answer a direct connection; the proxied path is checked with a CONNECT roundtrip
    /// (chain not up — the check is skipped).
    /// </summary>
    public async Task<BanLiveChecks> LiveChecksAsync(CancellationToken ct)
    {
        var probe = config.Guard.BanProbeName;

        var (dnsBlocked, dnsDetail) = await ProbeDnsAsync(probe, ct);
        var (directBlocked, directDetail) = await ProbeDirectAsync(probe, ct);
        bool? proxiedAlive = null;
        var proxiedDetail = "chain is not responding — proxied check skipped";
        try
        {
            if (await chainHealth(ct))
            {
                proxiedAlive = true;
                proxiedDetail = "proxied path is alive (CONNECT roundtrip)";
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or TaskCanceledException)
        {
            proxiedDetail = $"proxied check failed: {ex.Message}";
        }

        return new BanLiveChecks(dnsBlocked, dnsDetail, directBlocked, directDetail, proxiedAlive, proxiedDetail);
    }

    private static async Task<(bool Blocked, string Detail)> ProbeDnsAsync(string probe, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(probe, ct);
            return (false, $"DNS {probe}: resolves to {string.Join(",", addresses.Select(a => a.ToString()))}");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return (true, $"DNS {probe}: does not resolve");
        }
    }

    private static async Task<(bool Blocked, string Detail)> ProbeDirectAsync(string probe, CancellationToken ct)
    {
        // without the system proxy: SocketsHttpHandler reads WinINET by default — disable that
        using var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        try
        {
            using var response = await http.GetAsync($"https://{probe}/", ct);
            return (false, $"direct https://{probe}: HTTP {(int)response.StatusCode} — direct access is OPEN");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return (true, $"direct https://{probe}: blocked ({ex.InnerException?.Message ?? ex.Message})");
        }
    }
}
