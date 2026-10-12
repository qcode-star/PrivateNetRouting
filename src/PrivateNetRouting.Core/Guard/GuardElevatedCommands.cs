using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// Elevated guard commands: hosts/NRPT/firewall and the HKLM policy require administrator.
/// They run in a runas copy of the application (like the chain commands), the result is a nonce file.
/// </summary>
public static class GuardElevatedCommands
{
    public const string BanOn = "--elevated-guard-ban-on";
    public const string BanOff = "--elevated-guard-ban-off";
    public const string PolicyOn = "--elevated-guard-policy-on";
    public const string PolicyOff = "--elevated-guard-policy-off";
    // the combined action of the collapsed guards card: ban + policy in one elevation (one UAC)
    public const string AllOn = "--elevated-guard-all-on";
    public const string AllOff = "--elevated-guard-all-off";

    public static bool IsCommand(string argument) =>
        argument is BanOn or BanOff or PolicyOn or PolicyOff or AllOn or AllOff;

    /// <summary>Runs in an already elevated process (App or pnr); returns the exit code.</summary>
    public static int Execute(string command, string resultFile, string? configPath = null)
    {
        try
        {
            var config = AppConfigStore.LoadOrCreate(configPath);
            var log = new FileLog(
                Path.Combine(config.Logging.Directory, "guard-elevated.log"),
                config.Logging.EffectiveLevel);
            var powershell = new PowerShellRunner(config.PowerShellPath);
            var detail = command switch
            {
                BanOn => ApplyBanLayers(config, log, new PowerShellBanSystemOps(config, powershell), enable: true),
                BanOff => ApplyBanLayers(config, log, new PowerShellBanSystemOps(config, powershell), enable: false),
                PolicyOn => ApplyPolicy(config, new RegistryEgressPolicyStore(), enable: true),
                PolicyOff => ApplyPolicy(config, new RegistryEgressPolicyStore(), enable: false),
                AllOn => ApplyAll(config, log, new PowerShellBanSystemOps(config, powershell), new RegistryEgressPolicyStore(), enable: true),
                AllOff => ApplyAll(config, log, new PowerShellBanSystemOps(config, powershell), new RegistryEgressPolicyStore(), enable: false),
                _ => $"unknown command: {command}",
            };
            log.Info($"elevated {command}: {detail}");
            File.WriteAllText(resultFile, $"ok {detail}");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(resultFile, $"fail {ex.Message}");
            }
            catch (IOException)
            {
            }

            return 1;
        }
    }

    /// <summary>
    /// Three layers of the ban: the hosts block is rebuilt from the union of the app lists,
    /// the NRPT rule and the firewall group are removed and re-created (idempotent), the DNS cache is
    /// flushed; after applying, every probe name must not resolve.
    /// </summary>
    internal static string ApplyBanLayers(AppConfig config, FileLog log, IBanSystemOps ops, bool enable)
    {
        var guard = config.Guard;
        if (enable)
        {
            HostsFileBlock.Apply(guard.HostsFile, guard.HostsBeginMarker, guard.HostsEndMarker, guard.HostsComment, config.BanHosts);
            ops.ApplyNrptAsync(CancellationToken.None).GetAwaiter().GetResult();
            ops.ApplyFirewallAsync(CancellationToken.None).GetAwaiter().GetResult();
            ops.FlushDnsCacheAsync(CancellationToken.None).GetAwaiter().GetResult();
            var probes = guard.BanProbes.Count > 0 ? guard.BanProbes : ["www.anthropic.com"];
            var blocked = probes.ToDictionary(probe => probe, ProbeBlocked);
            if (blocked.Any(pair => !pair.Value))
            {
                var offenders = string.Join(", ", blocked.Where(pair => !pair.Value).Select(pair => pair.Key));
                log.Warn($"elevated ban: probes still resolve ({offenders}) — subtrees are not closed");
            }

            return $"hosts {config.BanHosts.Count} names, NRPT rule on {config.BanTrees.Count * 2} namespaces, "
                + $"firewall {config.FirewallSubnets.Count} subnets; probes "
                + string.Join(", ", blocked.Select(pair => $"{pair.Key} {(pair.Value ? "does not resolve" : "STILL RESOLVES")}"));
        }
        else
        {
            HostsFileBlock.Remove(guard.HostsFile, guard.HostsBeginMarker, guard.HostsEndMarker);
            ops.RemoveNrptAsync(CancellationToken.None).GetAwaiter().GetResult();
            ops.RemoveFirewallAsync(CancellationToken.None).GetAwaiter().GetResult();
            ops.FlushDnsCacheAsync(CancellationToken.None).GetAwaiter().GetResult();
            return "ban removed: hosts block, NRPT rule, firewall rules; DNS cache flushed";
        }
    }

    /// <summary>
    /// The combined action of the collapsed guards card: the three ban layers and the managed
    /// policy in one elevation — "enable/disable all" costs one UAC prompt.
    /// </summary>
    internal static string ApplyAll(AppConfig config, FileLog log, IBanSystemOps ops, IEgressPolicyStore policy, bool enable)
    {
        var ban = ApplyBanLayers(config, log, ops, enable);
        var policyDetail = ApplyPolicy(config, policy, enable);
        return $"{ban}; {policyDetail}";
    }

    private static string ApplyPolicy(AppConfig config, bool enable) =>
        ApplyPolicy(config, new RegistryEgressPolicyStore(), enable);

    internal static string ApplyPolicy(AppConfig config, IEgressPolicyStore policy, bool enable)
    {
        if (enable)
        {
            var url = ClaudeEnvValues.WindowsProxyUrl(config);
            policy.Apply(url);
            return $"egressProxyUrl={url}, disableWslSessions=0";
        }

        policy.Remove();
        return "egressProxyUrl and disableWslSessions removed";
    }

    /// <summary>Whether the probe name resolves right now (system resolver: hosts + NRPT); timeout is 5 s.</summary>
    internal static bool ProbeBlocked(string probeName)
    {
        try
        {
            var resolved = Task.Run(() => Dns.GetHostAddresses(probeName));
            return !resolved.Wait(TimeSpan.FromSeconds(5)) || resolved.Result.Length == 0;
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException)
        {
            return true;
        }
    }
}
