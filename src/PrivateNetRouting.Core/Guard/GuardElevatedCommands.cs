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

    public static bool IsCommand(string argument) =>
        argument is BanOn or BanOff or PolicyOn or PolicyOff;

    /// <summary>Runs in an already elevated process (App or pnr); returns the exit code.</summary>
    public static int Execute(string command, string resultFile, string? configPath = null)
    {
        try
        {
            var config = AppConfigStore.LoadOrCreate(configPath);
            var log = new FileLog(
                Path.Combine(config.Logging.Directory, "guard-elevated.log"),
                config.Logging.EffectiveLevel);
            var powershell = new PowerShellRunner(config.Updater.PowerShellPath);
            var detail = command switch
            {
                BanOn => ApplyBanLayers(config, log, new PowerShellBanSystemOps(config.Guard, powershell), enable: true),
                BanOff => ApplyBanLayers(config, log, new PowerShellBanSystemOps(config.Guard, powershell), enable: false),
                PolicyOn => ApplyPolicy(config, enable: true),
                PolicyOff => ApplyPolicy(config, enable: false),
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
    /// Three layers of the ban (port of ban-ai-hosts.ps1): the hosts block is rebuilt from the list,
    /// the NRPT rule and the firewall group are removed and re-created (idempotent), the DNS cache is
    /// flushed; after applying, a probe subtree name must not resolve.
    /// </summary>
    internal static string ApplyBanLayers(AppConfig config, FileLog log, IBanSystemOps ops, bool enable)
    {
        var guard = config.Guard;
        if (enable)
        {
            HostsFileBlock.Apply(guard.HostsFile, guard.HostsBeginMarker, guard.HostsEndMarker, guard.HostsComment, guard.BanHosts);
            ops.ApplyNrptAsync(CancellationToken.None).GetAwaiter().GetResult();
            ops.ApplyFirewallAsync(CancellationToken.None).GetAwaiter().GetResult();
            ops.FlushDnsCacheAsync(CancellationToken.None).GetAwaiter().GetResult();
            var probeBlocked = ProbeBlocked(guard.BanProbeName);
            if (!probeBlocked)
            {
                log.Warn($"elevated ban: probe {guard.BanProbeName} still resolves — subtrees are not closed");
            }

            return $"hosts {guard.BanHosts.Count} names, NRPT rule on {guard.BanTrees.Count * 2} namespaces, "
                + $"firewall {guard.FirewallSubnets.Count} subnets; probe {guard.BanProbeName} "
                + (probeBlocked ? "does not resolve" : "STILL RESOLVES (check NRPT)");
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

    private static string ApplyPolicy(AppConfig config, bool enable)
    {
        var policy = new RegistryEgressPolicyStore();
        if (enable)
        {
            var url = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy);
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
