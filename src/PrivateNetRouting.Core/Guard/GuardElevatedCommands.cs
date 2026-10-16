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
    public static int Execute(string command, string resultFile, string? configPath = null, IReadOnlyList<string>? profileEnvironment = null)
    {
        // an administrator must not write wherever the caller points: only our own result file shape
        if (!ElevatedHelperTrust.IsValidResultFile(resultFile))
        {
            return 1;
        }

        FileLog? log = null;
        try
        {
            if (configPath is not null && !ElevatedHelperTrust.IsValidConfigPath(configPath))
            {
                throw new InvalidOperationException("the config path of the elevated helper is not an absolute .json path");
            }

            // the paths are expanded with the asking user's profile variables, not the administrator's
            var config = AppConfigStore.LoadOrCreate(configPath, ElevatedHelperArguments.ToProfileEnvironment(profileEnvironment));
            log = new FileLog(
                Path.Combine(config.Logging.Directory, "guard-elevated.log"),
                config.Logging.EffectiveLevel);

            // the config is writable by any process of the user; what runs and is overwritten as
            // administrator is pinned to the system locations instead of read from it
            PinSystemLocations(config, log);
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
            ElevatedHelperTrust.WriteResultFile(resultFile, $"ok {detail}");
            return 0;
        }
        catch (Exception ex)
        {
            log?.Error($"elevated {command}: failed: {ex.Message}");
            try
            {
                ElevatedHelperTrust.WriteResultFile(resultFile, $"fail {ex.Message}");
            }
            catch (IOException)
            {
            }

            return 1;
        }
    }

    /// <summary>Overrides the config's PowerShell and hosts file with the system ones (logged when they differed).</summary>
    internal static void PinSystemLocations(AppConfig config, FileLog log)
    {
        var powershell = ElevatedHelperTrust.SystemPowerShell();
        if (!string.Equals(Path.GetFullPath(config.PowerShellPath), powershell, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"elevated helper: powerShellPath '{config.PowerShellPath}' ignored, using the system PowerShell");
        }

        var hosts = ElevatedHelperTrust.SystemHostsFile();
        if (!string.Equals(Path.GetFullPath(config.Guard.HostsFile), hosts, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"elevated helper: guard.hostsFile '{config.Guard.HostsFile}' ignored, using the system hosts file");
        }

        config.PowerShellPath = powershell;
        config.Guard.HostsFile = hosts;
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
            FlushTolerantly(ops, log);
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
            // a hosts file the block cannot be cut out of (a begin marker without its end) is
            // refused, but that must not keep the other layers in place: they are removed anyway
            // and the hosts failure is reported afterwards
            IOException? hostsFailure = null;
            try
            {
                HostsFileBlock.Remove(guard.HostsFile, guard.HostsBeginMarker, guard.HostsEndMarker);
            }
            catch (IOException ex)
            {
                hostsFailure = ex;
                log.Error($"elevated ban off: hosts block left in place: {ex.Message}");
            }

            ops.RemoveNrptAsync(CancellationToken.None).GetAwaiter().GetResult();
            ops.RemoveFirewallAsync(CancellationToken.None).GetAwaiter().GetResult();
            FlushTolerantly(ops, log);
            if (hostsFailure is not null)
            {
                throw new BanPartiallyRemovedException(
                    $"NRPT rule and firewall rules removed; the hosts block was NOT removed: {hostsFailure.Message}",
                    hostsFailure);
            }

            return "ban removed: hosts block, NRPT rule, firewall rules; DNS cache flushed";
        }
    }

    /// <summary>The ban removal reached every layer it could, but the hosts block stayed in place.</summary>
    internal sealed class BanPartiallyRemovedException(string message, Exception inner) : IOException(message, inner);

    /// <summary>
    /// The DNS cache flush is the last, cosmetic step: its failure must not report the whole
    /// operation failed after every layer was applied — a warning is enough.
    /// </summary>
    private static void FlushTolerantly(IBanSystemOps ops, FileLog log)
    {
        try
        {
            ops.FlushDnsCacheAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Warn($"elevated ban: the DNS cache flush failed (the layers are applied): {ex.Message}");
        }
    }

    /// <summary>
    /// The combined action of the collapsed guards card: the three ban layers and the managed
    /// policy in one elevation — "enable/disable all" costs one UAC prompt. On disable a hosts
    /// block that could not be removed does not keep the policy: it is removed too, and the
    /// failure is reported after it.
    /// </summary>
    internal static string ApplyAll(AppConfig config, FileLog log, IBanSystemOps ops, IEgressPolicyStore policy, bool enable)
    {
        string ban;
        try
        {
            ban = ApplyBanLayers(config, log, ops, enable);
        }
        catch (BanPartiallyRemovedException ex) when (!enable)
        {
            var removed = ApplyPolicy(config, policy, enable);
            throw new BanPartiallyRemovedException($"{removed}; {ex.Message}", ex.InnerException ?? ex);
        }

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
