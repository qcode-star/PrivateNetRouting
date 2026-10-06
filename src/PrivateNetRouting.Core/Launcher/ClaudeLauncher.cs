using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using Microsoft.Win32;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>Launch mode: normal or "check/bring up the chain only" (Claude does not start).</summary>
public sealed record LauncherOptions(bool CheckOnly)
{
    public static LauncherOptions Default { get; } = new(CheckOnly: false);
}

public enum LaunchOutcome
{
    /// <summary>Claude Desktop is started with the proxy flags.</summary>
    Launched,

    /// <summary>--check-only mode: the chain is alive, Claude was not started.</summary>
    CheckOnly,
}

public sealed record LaunchResult(LaunchOutcome Outcome, bool ChainRaised, TimeSpan Elapsed);

/// <summary>
/// Building --proxy-bypass-list: a fixed base list (localhost and private
/// ranges) plus config domains, each in two forms —
/// in Chromium 'dom.com' matches only the domain itself, while '*.dom.com' matches only subdomains.
/// </summary>
public static class ClaudeBypassList
{
    private static readonly string[] BaseParts =
    [
        "<local>", "localhost", "127.0.0.1", "[::1]",
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "*.local",
    ];

    public static string Build(IEnumerable<string> domains)
    {
        var parts = new List<string>(BaseParts);
        foreach (var domain in domains)
        {
            var zone = domain.Trim().TrimStart('.');
            if (zone.Length == 0)
            {
                continue;
            }

            parts.Add(zone);
            parts.Add("*." + zone);
        }

        return string.Join(";", parts);
    }
}

/// <summary>
/// Claude Desktop launcher: checks the chain with a live
/// CONNECT roundtrip, on failure brings it up idempotently (the only UAC window is
/// starting the daemon), then starts Claude with --proxy-server/--proxy-bypass-list.
/// The managed egressProxyUrl policy and already running Claude copies are only monitored,
/// with a warning in the log (they cannot stop the launch).
/// </summary>
public sealed class ClaudeLauncher(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IProcessFinder processFinder,
    FileLog log,
    Func<string?>? readEgressPolicy = null,
    Func<ProcessStartInfo, Process?>? startProcess = null)
{
    private const string PolicyKey = @"SOFTWARE\Policies\Claude";
    private const string PolicyValue = "egressProxyUrl";

    private readonly Func<string?> _readEgressPolicy = readEgressPolicy ?? ReadEgressPolicyFromRegistry;
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? (psi => Process.Start(psi));

    /// <summary>Launcher log (the splash screen offers to open it on failure).</summary>
    public FileLog Log => log;

    /// <summary>Proxy URL for --proxy-server and for comparison with the managed policy.</summary>
    public string ProxyUrl => "http://" + config.ForwardProxy.Listen;

    public string BuildBypassList() => ClaudeBypassList.Build(config.Launcher.BypassDomains);

    public async Task<LaunchResult> LaunchAsync(LauncherOptions options, IProgress<ChainStep>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var chainRaised = false;
        if (await healthCheck(ct))
        {
            log.Info($"launcher: proxy {config.ForwardProxy.Listen} responds");
        }
        else
        {
            log.Info("launcher: proxy is not responding — bringing the chain up");
            await chain.EnsureRunningAsync(progress, ct);
            chainRaised = true;
        }

        if (options.CheckOnly)
        {
            log.Info("launcher: check-only, Claude is not started");
            return new LaunchResult(LaunchOutcome.CheckOnly, chainRaised, watch.Elapsed);
        }

        WarnIfPolicyMissing();
        WarnIfClaudeRunningWithoutProxy();

        var info = new ProcessStartInfo(config.Launcher.AliasPath)
        {
            UseShellExecute = true,
        };
        info.ArgumentList.Add($"--proxy-server={ProxyUrl}");
        info.ArgumentList.Add($"--proxy-bypass-list={BuildBypassList()}");
        try
        {
            using var started = _startProcess(info);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            throw new IOException($"failed to start Claude ({config.Launcher.AliasPath}): {ex.Message}", ex);
        }

        log.Info("launcher: Claude started with --proxy-server/--proxy-bypass-list");
        return new LaunchResult(LaunchOutcome.Launched, chainRaised, watch.Elapsed);
    }

    /// <summary>
    /// Safeguard against a launch not via the launcher: the managed policy takes effect for any launch
    /// method, the flags only for ours. A missing/mismatched policy is a warning
    /// in the log (it is set by a separate Guard module and requires admin rights).
    /// </summary>
    private void WarnIfPolicyMissing()
    {
        string? policy;
        try
        {
            policy = _readEgressPolicy();
        }
        catch (Exception ex)
        {
            log.Warn($"launcher: egress policy read failed: {ex.Message}");
            return;
        }

        if (policy == ProxyUrl)
        {
            log.Info($"launcher: managed policy {PolicyValue} is in place");
        }
        else
        {
            log.Warn($"launcher: managed policy {PolicyValue} is missing or different [{policy ?? ""}] — a start outside the launcher goes direct");
        }
    }

    /// <summary>An already running Claude without --proxy-server would work around the proxy: warn.</summary>
    private void WarnIfClaudeRunningWithoutProxy()
    {
        IReadOnlyList<FoundProcess> processes;
        try
        {
            processes = processFinder.FindByNameContains("claude");
        }
        catch (Exception ex)
        {
            log.Warn($"launcher: process scan failed: {ex.Message}");
            return;
        }

        if (processes.Count == 0)
        {
            return;
        }

        var withProxy = processes.Count(p => p.CommandLine.Contains("--proxy-server=", StringComparison.Ordinal));
        if (withProxy == 0)
        {
            log.Warn($"launcher: Claude is already running ({processes.Count} processes) without --proxy-server — close it and use the launcher again");
        }
        else
        {
            log.Info($"launcher: Claude is already running ({processes.Count} processes) — starting one more instance activates the existing window");
        }
    }

    private static string? ReadEgressPolicyFromRegistry() =>
        Registry.GetValue($"HKEY_LOCAL_MACHINE\\{PolicyKey}", PolicyValue, null) as string;
}
