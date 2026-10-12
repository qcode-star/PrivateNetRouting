using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using Microsoft.Win32;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>
/// Launch mode: normal or "check/bring up the chain only" (the application does not start).
/// RestartRunning closes an already running copy before the start: without it a second start of
/// a single-instance app only activates the existing window and the proxy flags never apply.
/// The owner is asked about the restart by the caller (GUI dialog, CLI prompt) — this flag is
/// the answer to that question, not a decision of the launcher itself.
/// </summary>
public sealed record LauncherOptions(bool CheckOnly, bool RestartRunning = false)
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

public sealed record LaunchResult(LaunchOutcome Outcome, bool ChainRaised, TimeSpan Elapsed, bool RestartedExisting = false);

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
/// The managed egressProxyUrl policy is only monitored, with a warning in the log (it cannot
/// stop the launch). An already running copy is reported the same way, and with
/// LauncherOptions.RestartRunning it is closed first — the flags of a second start otherwise
/// never reach the processes of the first one.
/// </summary>
public sealed class ClaudeLauncher(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IProcessFinder processFinder,
    FileLog log,
    Func<string?>? readEgressPolicy = null,
    Func<ProcessStartInfo, Process?>? startProcess = null,
    IRunningAppCloser? appCloser = null)
{
    private const string PolicyKey = @"SOFTWARE\Policies\Claude";
    private const string PolicyValue = "egressProxyUrl";

    /// <summary>The launcher has no install path of its own to check against (unlike the Codex package name).</summary>
    private const string ProcessNamePart = "claude";

    private readonly Func<string?> _readEgressPolicy = readEgressPolicy ?? ReadEgressPolicyFromRegistry;
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? (psi => Process.Start(psi));
    private readonly RunningApp _running = new(
        processFinder,
        ProcessNamePart,
        commandLineMarker: null,
        AppProxyEndpoint.Url(config),
        appCloser ?? new WindowsRunningAppCloser(),
        log,
        "launcher");

    /// <summary>Launcher log (the splash screen offers to open it on failure).</summary>
    public FileLog Log => log;

    /// <summary>Proxy URL for --proxy-server and for comparison with the managed policy.</summary>
    public string ProxyUrl => AppProxyEndpoint.Url(config);

    public string BuildBypassList() => ClaudeBypassList.Build(config.BypassDomains);

    public async Task<LaunchResult> LaunchAsync(LauncherOptions options, IProgress<ChainStep>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var chainRaised = false;
        if (await healthCheck(ct))
        {
            log.Info($"launcher: proxy {AppProxyEndpoint.Address(config)} responds");
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

        var running = _running.Inspect();
        var restarted = false;
        if (options.RestartRunning && running.State == RunningAppState.WithoutProxy)
        {
            // The owner has already agreed to the restart (GUI dialog, CLI question): a copy
            // that carries our proxy is left alone — a new start only activates its window.
            restarted = (await _running
                    .CloseAsync("Claude", TimeSpan.FromSeconds(config.RestartGraceSeconds), ct)
                    .ConfigureAwait(false))
                .AnythingClosed;
        }
        else
        {
            _running.Report(running, "Claude");
        }

        var info = new ProcessStartInfo(config.Claude.AliasPath)
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
            throw new IOException($"failed to start Claude ({config.Claude.AliasPath}): {ex.Message}", ex);
        }

        log.Info("launcher: Claude started with --proxy-server/--proxy-bypass-list");
        return new LaunchResult(LaunchOutcome.Launched, chainRaised, watch.Elapsed, restarted);
    }

    /// <summary>
    /// The already-running state of Claude: a copy started outside the launcher keeps its own
    /// flags until it is closed, so the GUI and the CLI ask the owner about a restart before
    /// the launch (LauncherOptions.RestartRunning).
    /// </summary>
    public RunningAppInfo InspectRunning() => _running.Inspect();

    /// <summary>The owner has refused the restart offer: the log keeps the reason nothing started.</summary>
    public void NoteRestartDeclined() => _running.NoteDeclined("Claude");

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

    private static string? ReadEgressPolicyFromRegistry() =>
        Registry.GetValue($"HKEY_LOCAL_MACHINE\\{PolicyKey}", PolicyValue, null) as string;
}
