using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Guard;

/// <summary>Живые проверки бана (порт Get-LiveChecks ban-ai-hosts.ps1): только чтение, прав не требует.</summary>
public sealed record BanLiveChecks(
    bool DnsBlocked,
    string DnsDetail,
    bool DirectBlocked,
    string DirectDetail,
    bool? ProxiedAlive,
    string ProxiedDetail);

public sealed record BanApplyResult(string Detail, BanLiveChecks Live);

public sealed record SettingsSyncResult(string? BackupPath, string ProxyUrl, string NoProxy);

public sealed record GuardSnapshot(
    LocalBanStatus Ban,
    EgressPolicyStatus Policy,
    ClaudeSettingsStatus Settings,
    WslConfigStatus Wsl,
    SystemProxyStatus SystemProxy,
    IReadOnlyList<GuardItemStatus> Items)
{
    public GuardItemStatus this[GuardKind kind] => Items.First(item => item.Kind == kind);
}

/// <summary>
/// Оркестрация гардов прямого доступа: статус — только чтением (реестр, hosts, NRPT/firewall
/// командлетами, settings.json, WSL), включение бана/политики — элевейтнутым helper'ом,
/// settings.json/WSL/системный прокси — без элевации. После применения бана выполняются живые
/// проверки (правило №6 AGENTS.md: не «должно работать», а проверка на живом трафике).
/// </summary>
public sealed class GuardService(
    AppConfig config,
    IEgressPolicyStore policy,
    IBanSystemOps banOps,
    ClaudeSettingsGuard settings,
    WslConfigGuard wsl,
    ISystemProxyStore systemProxy,
    IElevatedCommandRunner elevated,
    FileLog log,
    Func<CancellationToken, Task<bool>> chainHealth)
{
    public async Task<GuardSnapshot> GetStatusAsync(CancellationToken ct)
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

        var settingsStatus = settings.Read();
        var wslStatus = await wsl.ReadAsync(ct);
        var sysProxyStatus = systemProxy.Read();

        return new GuardSnapshot(ban, policyStatus, settingsStatus, wslStatus, sysProxyStatus, BuildItems(ban, policyStatus, settingsStatus, wslStatus, sysProxyStatus));
    }

    private static LocalBanStatus UnknownBan(Exception ex) =>
        new LocalBanStatus(GuardState.Unknown, 0, false, 0, [], 0, []) { Detail = ex.Message };

    private static EgressPolicyStatus UnknownPolicy(Exception ex) =>
        new EgressPolicyStatus(GuardState.Unknown, null, "", null) { Detail = ex.Message };

    /// <summary>Готовые английские строки статуса для CLI; GUI форматирует из типизированных полей.</summary>
    internal static IReadOnlyList<GuardItemStatus> BuildItems(
        LocalBanStatus ban,
        EgressPolicyStatus policy,
        ClaudeSettingsStatus settings,
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
            new(GuardKind.WslConfig, wsl.State, wsl.Distros.Count == 0
                ? wsl.Reason
                : string.Join("; ", wsl.Distros.Select(d =>
                    $"{d.Distro}: {Word(d.State)}{(d.Sync ? "" : " (sync off)")}{(d.Reason.Length > 0 ? $" - {d.Reason}" : "")}"))),
            new(GuardKind.SystemProxy, sysProxy.Enabled ? GuardState.On : GuardState.Off,
                sysProxy.Enabled ? $"enabled: {sysProxy.Server}" : "disabled"),
        ];
    }


    internal static string Word(GuardState state) => state switch
    {
        GuardState.On => "on",
        GuardState.Off => "off",
        GuardState.Partial => "partial",
        _ => "unknown",
    };

    /// <summary>Включение/снятие трёх слоёв бана (UAC) с живыми проверками после применения.</summary>
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

    /// <summary>Включение/снятие managed-политики (UAC); после — контрольное чтение.</summary>
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

    public Task<SettingsSyncResult> SyncSettingsAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            var backup = settings.Sync();
            var outcome = new SettingsSyncResult(
                backup,
                ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy),
                ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains));
            log.Info($"guard settings sync: env proxy {outcome.ProxyUrl}, NO_PROXY written (backup: {backup ?? "none"})");
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
                log.Info(distro.Skipped
                    ? $"guard wsl sync: {distro.Distro} skipped ({distro.Reason})"
                    : $"guard wsl sync: {distro.Distro} proxy {distro.ProxyUrl}, settings+script written");
            }
        }

        return result;
    }

    /// <summary>Системный прокси WinINET (прав не требует); выключатель возвращает прямой доступ.</summary>
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
    /// Живые проверки (порт Get-LiveChecks): пробное имя не должно резолвиться и не должно
    /// отвечать прямым соединением; проксируемый путь проверяется CONNECT-roundtrip'ом
    /// (цепочка не поднята — проверка пропускается, как в прежней схеме).
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
        // без системного прокси: SocketsHttpHandler по умолчанию читает WinINET — отключаем
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
