using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// Live ban checks: read-only, requires no rights. A verdict is true (blocked), false (open) or
/// null - not established: no probe names configured, the network could not be judged (the DNS
/// server itself was unreachable), or the direct probe never got past the name (a name the ban
/// already blocks says nothing about the firewall layer).
/// </summary>
public sealed record BanLiveChecks(
    bool? DnsBlocked,
    string DnsDetail,
    bool? DirectBlocked,
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
    string CodexPath,
    bool CodexEnvWritten,
    string? CodexEnvBackupPath,
    string CodexEnvPath,
    string? ClaudeError = null,
    string? CodexError = null)
{
    /// <summary>claude.enabled=false: Claude Code settings.json was not touched.</summary>
    public bool ClaudeSkipped { get; init; }

    /// <summary>codex.enabled=false: config.toml and .env of Codex were not touched (nor created).</summary>
    public bool CodexSkipped { get; init; }
}

/// <summary>
/// The outcome of "enable all": every step ran, each one's failure is its own field (null — the
/// step did not throw), and the live ban checks are always there.
/// </summary>
public sealed record GuardEnableAllResult(
    string? ElevatedDetail,
    string? ElevatedError,
    SettingsSyncResult? Settings,
    string? SettingsError,
    WslSyncResult? Wsl,
    string? WslError,
    BanLiveChecks Live)
{
    /// <summary>The settings failures of both applications and of the step itself.</summary>
    public IReadOnlyList<string> SettingsErrors =>
        new[] { SettingsError, Settings?.ClaudeError, Settings?.CodexError }.OfType<string>().ToList();

    /// <summary>The WSL step failed as a whole or for at least one distro.</summary>
    public bool WslFailed => WslError is not null || Wsl?.Distros.Any(d => d.Failed) == true;
}

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

    /// <summary>claude.enabled: false — the Claude half of the settings layer is not judged.</summary>
    public bool ClaudeEnabled { get; init; } = true;

    /// <summary>codex.enabled: false — the Codex half of the settings layer is not judged.</summary>
    public bool CodexEnabled { get; init; } = true;
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

        // one failing layer must not take the whole snapshot down (the WSL sync isolates per distro
        // the same way): a throwing layer is reported as Unknown with the reason — a malformed
        // settings file is surfaced (Q-02) and left alone
        var expectedProxy = ClaudeEnvValues.WindowsProxyUrl(config);
        var expectedNoProxy = ClaudeEnvValues.BuildNoProxy(config.BypassDomains);

        // an application off in the config is neither synced nor judged: its half of the layer is
        // reported as such instead of the state of files nobody maintains
        ClaudeSettingsStatus settingsStatus;
        try
        {
            settingsStatus = config.Claude.Enabled
                ? settings.Read()
                : new ClaudeSettingsStatus(GuardState.Off, null, null, expectedProxy, expectedNoProxy, FileMissing: false)
                {
                    Detail = DisabledDetail("claude"),
                };
        }
        catch (Exception ex)
        {
            settingsStatus = new ClaudeSettingsStatus(GuardState.Unknown, null, null, expectedProxy, expectedNoProxy, FileMissing: false)
            {
                Detail = ex.Message,
            };
        }

        CodexSettingsStatus codexStatus;
        try
        {
            codexStatus = config.Codex.Enabled
                ? codexSettings.Read()
                : new CodexSettingsStatus(GuardState.Off, null, FileMissing: false, EnvUpToDate: false) { Detail = DisabledDetail("codex") };
        }
        catch (Exception ex)
        {
            codexStatus = new CodexSettingsStatus(GuardState.Unknown, null, FileMissing: false, EnvUpToDate: false) { Detail = ex.Message };
        }

        WslConfigStatus wslStatus;
        try
        {
            wslStatus = await wsl.ReadAsync(ct);
        }
        catch (Exception ex)
        {
            wslStatus = new WslConfigStatus(GuardState.Unknown, [], 0, null, $"failed to read: {ex.Message}");
        }

        SystemProxyStatus sysProxyStatus;
        try
        {
            sysProxyStatus = systemProxy.Read();
        }
        catch (Exception ex)
        {
            sysProxyStatus = new SystemProxyStatus(false, null, null) { Detail = ex.Message };
        }

        return new GuardSnapshot(
            ban,
            policyStatus,
            settingsStatus,
            codexStatus,
            wslStatus,
            sysProxyStatus,
            BuildItems(ban, policyStatus, settingsStatus, codexStatus, wslStatus, sysProxyStatus))
        {
            ClaudeEnabled = config.Claude.Enabled,
            CodexEnabled = config.Codex.Enabled,
        };
    }

    private static string DisabledDetail(string app) => $"{app}.enabled=false - not synced";

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
            var hosts = HostsFileBlock.Read(guard.HostsFile, guard.HostsBeginMarker, guard.HostsEndMarker, guard.HostsComment, config.BanHosts);
            var layers = await banOps.GetLayersStateAsync(ct);
            var hostsLayerOn = hosts.Present;

            // a layer that is there but lags behind the config (a domain or a subnet added since)
            // is not "on": the card then offers "enable all", which re-applies it
            var nrptCurrent = layers.NrptRules == 0 || BanLayersMatch.SameNamespaces(layers.NrptNamespaces, config.BanTrees);
            var firewallCurrent = layers.FirewallRules == 0 || BanLayersMatch.SameSubnets(layers.FirewallSubnets, config.FirewallSubnets);
            var allPresent = hostsLayerOn && layers.NrptRules > 0 && layers.FirewallRules > 0;
            var state = allPresent && hosts.Current && nrptCurrent && firewallCurrent ? GuardState.On
                : hostsLayerOn || layers.NrptRules > 0 || layers.FirewallRules > 0 ? GuardState.Partial
                : GuardState.Off;
            ban = new LocalBanStatus(
                state,
                hosts.EntryCount,
                hosts.Current,
                layers.NrptRules,
                layers.NrptNamespaces,
                layers.FirewallRules,
                layers.FirewallSubnets)
            {
                NrptCurrent = nrptCurrent,
                FirewallCurrent = firewallCurrent,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // whatever the reason (an unreadable hosts file, PowerShell that did not start, an
            // unexpected cmdlet answer), the ban alone is unknown: the other layers are still read
            ban = UnknownBan(ex);
        }

        EgressPolicyStatus policyStatus;
        try
        {
            var expected = ClaudeEnvValues.WindowsProxyUrl(config);
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
            new(GuardKind.LocalBan, ban.State, ban.Detail ?? $"hosts: {ban.HostsEntries} names{(ban.HostsCurrent || ban.HostsEntries == 0 ? "" : " (stale list)")}; NRPT: {ban.NrptRules} rule(s) on {ban.NrptNamespaces.Count} namespaces{(ban.NrptCurrent ? "" : " (stale list)")}; firewall: {ban.FirewallRules} rule(s) on {ban.FirewallSubnets.Count} subnets{(ban.FirewallCurrent ? "" : " (stale list)")}"),
            new(GuardKind.EgressPolicy, policy.State, policy.Detail ?? (policy.State == GuardState.On
                ? $"egressProxyUrl={policy.CurrentUrl}, disableWslSessions={policy.DisableWslSessions}"
                : policy.State == GuardState.Off
                    ? "policy is not set"
                    : $"egressProxyUrl={policy.CurrentUrl} (expected {policy.ExpectedUrl})")),
            new(GuardKind.ClaudeSettings, settings.State, settings.FileMissing
                ? "settings.json not found"
                : settings.Detail ?? (settings.State == GuardState.On
                    ? $"env proxy {settings.ExpectedProxy}, NO_PROXY, privacy and attribution keys up to date"
                    : string.Join("; ", new[]
                    {
                        settings.CurrentProxy == settings.ExpectedProxy && settings.CurrentNoProxy == settings.ExpectedNoProxy
                            ? null
                            : $"HTTPS_PROXY={settings.CurrentProxy ?? "<none>"}, NO_PROXY={settings.CurrentNoProxy ?? "<none>"} (expected {settings.ExpectedProxy})",
                        settings.Drift is { Count: > 0 } drift
                            ? "settings.json keys not set: " + string.Join(", ", drift)
                            : null,
                    }.Where(part => part is not null)))),
            new(GuardKind.CodexSettings, codex.State, codex.Detail ?? (codex.State switch
            {
                GuardState.On => "respect_system_proxy = true, .env proxy env up to date",
                GuardState.Off => "config.toml and .env not found",
                _ => $"respect_system_proxy={Word(codex.Enabled)}, .env proxy env {(codex.EnvUpToDate ? "up to date" : "stale or missing")}",
            })),
            new(GuardKind.WslConfig, wsl.State, wsl.Distros.Count == 0
                ? wsl.Reason
                : string.Join("; ", wsl.Distros.Select(d =>
                    $"{d.Distro}: {Word(d.State)}{(d.Sync ? "" : " (sync off)")} (claude script {(d.ScriptPort?.ToString() ?? "-")}, codex proxy env {(d.ProxyEnvPresent ? d.ProxyEnvPort?.ToString() ?? "present" : "missing")}){(d.Reason.Length > 0 ? $" - {d.Reason}" : "")}"))
                + (wsl.Warning is null ? "" : (wsl.Distros.Count == 0 && wsl.Reason.Length > 0 ? "; " : "") + wsl.Warning)),
            new(GuardKind.SystemProxy,
                sysProxy.Detail is not null
                    ? GuardState.Unknown
                    : !sysProxy.Enabled
                        ? GuardState.Off
                        : sysProxy.Ours ? GuardState.On : GuardState.Partial,
                sysProxy.Detail ?? (!sysProxy.Enabled
                    ? "disabled"
                    : sysProxy.Ours
                        ? $"enabled: {sysProxy.Server}"
                        : $"enabled by the owner or another tool: {sysProxy.Server} - not this application's proxy, off leaves it alone")),
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
    /// "Enable all", best-effort: ban and policy in one elevation, then the settings sync, then the
    /// WSL sync — a failing step does not keep the next ones from running, and its error travels in
    /// the result. The live ban checks run last whatever failed: the verdict on the direct path is
    /// the one thing a failure elsewhere must not hide.
    /// </summary>
    public async Task<GuardEnableAllResult> EnableAllAsync(CancellationToken ct)
    {
        string? elevatedDetail = null;
        string? elevatedError = null;
        try
        {
            var result = await elevated.RunAsync(GuardElevatedCommands.AllOn, ct);
            if (result.Ok)
            {
                elevatedDetail = result.Detail;
                log.Info($"guard all on: {result.Detail}");
            }
            else
            {
                elevatedError = result.Detail;
                log.Error($"guard all on: {result.Detail}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            elevatedError = ex.Message;
            log.Error($"guard all on: {ex.Message}");
        }

        SettingsSyncResult? settingsResult = null;
        string? settingsError = null;
        try
        {
            settingsResult = await SyncSettingsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            settingsError = ex.Message;
            log.Warn($"guard all on: settings sync failed: {ex.Message}");
        }

        WslSyncResult? wslResult = null;
        string? wslError = null;
        try
        {
            wslResult = await SyncWslAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            wslError = ex.Message;
            log.Warn($"guard all on: WSL sync failed: {ex.Message}");
        }

        var live = await LiveChecksAsync(ct);
        return new GuardEnableAllResult(elevatedDetail, elevatedError, settingsResult, settingsError, wslResult, wslError, live);
    }

    /// <summary>
    /// Both applications at once: env in Claude Code settings.json and the Codex layer — the
    /// system-proxy flag in config.toml plus the proxy env in .env (the guard card's single sync
    /// action). Each file is written only on an actual change; the log carries one line per file.
    /// One failing application does not prevent the other's write: each sync is isolated and its
    /// failure travels in the result (the WSL sync reports per distro the same way).
    /// </summary>
    public Task<SettingsSyncResult> SyncSettingsAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            string? claudeError = null;
            string? codexError = null;
            var sync = new ClaudeSettingsGuard.ClaudeSettingsSyncResult(false, null);
            var codexSync = new CodexSettingsGuard.CodexSettingsSyncResult(false, null, false, null);
            try
            {
                if (config.Claude.Enabled)
                {
                    sync = settings.Sync();
                }
            }
            catch (Exception ex)
            {
                claudeError = ex.Message;
                log.Warn($"guard settings sync: Claude settings.json failed: {ex.Message}");
            }

            try
            {
                // an application off in the config gets no files of its own: a Codex that is not
                // even installed must not find a fresh config.toml and .env created for it
                if (config.Codex.Enabled)
                {
                    codexSync = codexSettings.Sync();
                }
            }
            catch (Exception ex)
            {
                codexError = ex.Message;
                log.Warn($"guard settings sync: the Codex layer failed: {ex.Message}");
            }

            var outcome = new SettingsSyncResult(
                sync.Written,
                sync.BackupPath,
                ClaudeEnvValues.WindowsProxyUrl(config),
                ClaudeEnvValues.BuildNoProxy(config.BypassDomains),
                codexSync.Written,
                codexSync.BackupPath,
                config.Codex.SettingsPath,
                codexSync.EnvWritten,
                codexSync.EnvBackupPath,
                config.Codex.EnvPath,
                claudeError,
                codexError)
            {
                ClaudeSkipped = !config.Claude.Enabled,
                CodexSkipped = !config.Codex.Enabled,
            };
            if (outcome.ClaudeSkipped)
            {
                log.Info("guard settings sync: Claude settings.json skipped (claude.enabled=false)");
            }
            else if (claudeError is null)
            {
                log.Info(outcome.Written
                    ? $"guard settings sync: Claude settings.json written — proxy {outcome.ProxyUrl}, NO_PROXY, privacy and attribution keys (backup: {outcome.BackupPath ?? "none"})"
                    : $"guard settings sync: Claude settings.json already up to date (proxy {outcome.ProxyUrl})");
            }

            if (outcome.CodexSkipped)
            {
                log.Info("guard settings sync: the Codex layer skipped (codex.enabled=false)");
            }
            else if (codexError is null)
            {
                log.Info(outcome.CodexWritten
                    ? $"guard settings sync: respect_system_proxy=true written to config.toml (backup: {outcome.CodexBackupPath ?? "none"})"
                    : "guard settings sync: respect_system_proxy=true already in place");
                log.Info(outcome.CodexEnvWritten
                    ? $"guard settings sync: proxy env written to .env (backup: {outcome.CodexEnvBackupPath ?? "none"}) — the file the embedded CLI core loads itself"
                    : "guard settings sync: .env proxy env already in place");
            }

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
                if (distro.Failed)
                {
                    log.Warn($"guard wsl sync: {distro.Distro} {distro.Reason}");
                    continue;
                }

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
                var boot = new List<string>();
                if (distro.BootRunnerWritten)
                {
                    boot.Add("boot runner");
                }

                if (distro.UserCommandWritten)
                {
                    boot.Add("user boot command");
                }

                if (distro.WslConfWritten)
                {
                    boot.Add("wsl.conf entry");
                }

                if (boot.Count > 0)
                {
                    what += $", boot layer: {string.Join(" + ", boot)} written";
                }

                if (distro.ClaudeDirChowned)
                {
                    what += ", ~/.claude handed back from root to the user";
                }

                var backups = new[] { distro.SettingsBackup, distro.ScriptBackup }
                    .Concat(distro.BootBackups ?? [])
                    .Where(b => b is not null)
                    .ToList();
                log.Info($"guard wsl sync: {distro.Distro} proxy {distro.ProxyUrl}, {what}" +
                         (backups.Count > 0 ? $" (backup: {string.Join(", ", backups)})" : ""));
            }
        }

        return result;
    }

    /// <summary>WinINET system proxy (requires no rights); disabling restores direct access. Points at the applications proxy endpoint.</summary>
    public Task SetSystemProxyAsync(bool enable, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (enable)
            {
                var server = SystemProxyServer(config);
                var bypass = SystemProxyBypass.Build(config.BypassDomains, config.DirectTlds);
                systemProxy.Enable(server, bypass);
                log.Info($"guard sysproxy on: {server} (bypass {config.BypassDomains.Count} domains + {config.DirectTlds.Count} TLDs)");
            }
            else
            {
                // the address this installation writes while on: the store uses it to tell its own
                // leftover values from the user's
                var before = systemProxy.Read();
                systemProxy.Disable(SystemProxyServer(config));
                log.Info(before is { Enabled: true, Ours: false }
                    ? $"guard sysproxy off: {before.Server} is not this application's proxy - left alone"
                    : "guard sysproxy off");
            }
        }, ct);

    /// <summary>
    /// The ProxyServer value of the applications proxy endpoint: host:port, an IPv6 host in
    /// brackets (a bare "::1:18081" is not an address WinINET can split).
    /// </summary>
    internal static string SystemProxyServer(AppConfig config) => AppProxyEndpoint.EndPoint(config).ToString();

    /// <summary>
    /// Live checks: every probe name (guard.banProbes) must not resolve and must not answer a
    /// direct connection; the proxied path is checked with a CONNECT roundtrip
    /// (chain not up — the check is skipped).
    /// </summary>
    public async Task<BanLiveChecks> LiveChecksAsync(CancellationToken ct)
    {
        var probes = config.Guard.BanProbes;
        var dnsDetails = new List<string>();
        var directDetails = new List<string>();
        var dnsVerdicts = new List<bool?>();
        var directVerdicts = new List<bool?>();
        foreach (var probe in probes)
        {
            var (dns, dnsDetail) = await ProbeDnsAsync(probe, ct);
            var (direct, directDetail) = await ProbeDirectAsync(probe, dns, ct);
            dnsVerdicts.Add(dns);
            directVerdicts.Add(direct);
            dnsDetails.Add(dnsDetail);
            directDetails.Add(directDetail);
        }

        if (probes.Count == 0)
        {
            // an empty list used to read as a vacuous "blocked"
            dnsDetails.Add("DNS: no probe names configured (guard.banProbes) - not checked");
            directDetails.Add("direct: no probe names configured (guard.banProbes) - not checked");
        }

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

        return new BanLiveChecks(
            CombineVerdicts(dnsVerdicts), string.Join(" | ", dnsDetails),
            CombineVerdicts(directVerdicts), string.Join(" | ", directDetails),
            proxiedAlive, proxiedDetail);
    }

    /// <summary>
    /// One verdict over the probes: any open probe makes it open, otherwise any undecided one
    /// leaves it undecided, and only a list of blocked probes is blocked. No probes - no verdict.
    /// </summary>
    internal static bool? CombineVerdicts(IReadOnlyList<bool?> verdicts)
    {
        if (verdicts.Count == 0)
        {
            return null;
        }

        if (verdicts.Any(v => v == false))
        {
            return false;
        }

        return verdicts.Any(v => v is null) ? null : true;
    }

    /// <summary>
    /// The name is blocked only when the resolver answers "no such host / no data"; a resolver
    /// that cannot be reached (offline, DNS down) proves nothing about the ban.
    /// </summary>
    private static async Task<(bool? Blocked, string Detail)> ProbeDnsAsync(string probe, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(probe, ct);
            return (false, $"DNS {probe}: resolves to {string.Join(",", addresses.Select(a => a.ToString()))}");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            return (true, $"DNS {probe}: does not resolve");
        }
        catch (SocketException ex)
        {
            return (null, $"DNS {probe}: inconclusive - the resolver failed ({ex.SocketErrorCode}), not a block");
        }
    }

    /// <summary>
    /// Direct connection to the probe, no proxy. Blocked means the TCP connection itself failed
    /// or timed out; an answer, or a TLS error after connecting, means the path is open. When the
    /// name does not even resolve, the firewall layer was never exercised - no verdict.
    /// </summary>
    private static async Task<(bool? Blocked, string Detail)> ProbeDirectAsync(string probe, bool? dnsBlocked, CancellationToken ct)
    {
        if (dnsBlocked != false)
        {
            return (null, $"direct https://{probe}: not exercised - the name does not resolve here, so the firewall layer was not reached");
        }

        // without the system proxy: SocketsHttpHandler reads WinINET by default - disable that
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
            return (false, $"direct https://{probe}: HTTP {(int)response.StatusCode} - direct access is OPEN");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
        {
            return (true, $"direct https://{probe}: blocked ({ex.InnerException?.Message ?? ex.Message})");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            return (null, $"direct https://{probe}: not exercised - name resolution failed ({ex.InnerException?.Message ?? ex.Message})");
        }
        catch (HttpRequestException ex)
        {
            // the TCP connection was made (TLS, protocol, a reset after connect): the path is open
            return (false, $"direct https://{probe}: connected, then {ex.HttpRequestError} - direct access is OPEN");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (true, $"direct https://{probe}: blocked (no connection within the timeout)");
        }
    }
}
