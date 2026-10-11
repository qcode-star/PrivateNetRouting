using System.Diagnostics;
using System.Text;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Launcher;

public enum CodexLaunchOutcome
{
    /// <summary>The Codex desktop app is started with the proxy flags and env.</summary>
    Launched,

    /// <summary>--check-only mode: the chain is alive, Codex was not started.</summary>
    CheckOnly,
}

public sealed record CodexLaunchResult(CodexLaunchOutcome Outcome, bool ChainRaised, TimeSpan Elapsed, bool RestartedExisting = false);

/// <summary>
/// Codex desktop app (OpenAI, Microsoft Store MSIX) launcher: checks the chain with a live
/// CONNECT roundtrip, on failure brings it up idempotently (the only UAC window is starting the
/// daemon), then starts the app with --proxy-server/--proxy-bypass-list and the proxy env
/// variables. The launch goes through Invoke-CommandInDesktopPackage: the packaged entry-point
/// stub drops arguments, and the package-context launch is what hands the process both the
/// flags (the Chromium side of the app) and the env (its embedded CLI core takes the standard
/// proxy variables; config.toml has no proxy key). An already running app without the flags is
/// reported in the log, and with LauncherOptions.RestartRunning it is closed first — a second
/// start of a single-instance app only activates the existing window.
/// </summary>
public sealed class CodexLauncher(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IProcessFinder processFinder,
    FileLog log,
    IPowerShellExecutor powershell,
    Func<ProcessStartInfo, Process?>? startProcess = null,
    IRunningAppCloser? appCloser = null)
{
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? (psi => Process.Start(psi));

    /// <summary>
    /// The scan is filtered by the package name in the command line: a bare process name is not
    /// enough (another application may share the executable name), while the MSIX install path
    /// always carries the package name.
    /// </summary>
    private readonly RunningApp _running = new(
        processFinder,
        config.Codex.ProcessName,
        config.Codex.PackageName,
        "http://" + config.ForwardProxy.Listen,
        appCloser ?? new WindowsRunningAppCloser(),
        log,
        "codex-launcher");

    /// <summary>Launcher log.</summary>
    public FileLog Log => log;

    /// <summary>Proxy URL for --proxy-server and for the proxy env variables.</summary>
    public string ProxyUrl => "http://" + config.ForwardProxy.Listen;

    public string BuildBypassList() => ClaudeBypassList.Build(config.Launcher.BypassDomains);

    public async Task<CodexLaunchResult> LaunchAsync(LauncherOptions options, IProgress<ChainStep>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var chainRaised = false;
        if (await healthCheck(ct))
        {
            log.Info($"codex-launcher: proxy {config.ForwardProxy.Listen} responds");
        }
        else
        {
            log.Info("codex-launcher: proxy is not responding — bringing the chain up");
            await chain.EnsureRunningAsync(progress, ct);
            chainRaised = true;
        }

        if (options.CheckOnly)
        {
            log.Info("codex-launcher: check-only, Codex is not started");
            return new CodexLaunchResult(CodexLaunchOutcome.CheckOnly, chainRaised, watch.Elapsed);
        }

        var running = _running.Inspect();
        var restarted = false;
        if (options.RestartRunning && running.State == RunningAppState.WithoutProxy)
        {
            // The owner has already agreed to the restart (GUI dialog, CLI question): a second
            // start of a single-instance app would only activate the existing window, so the
            // running copy is closed first. A copy that carries our proxy is left alone.
            restarted = (await _running
                    .CloseAsync("Codex", TimeSpan.FromSeconds(config.Launcher.RestartGraceSeconds), ct)
                    .ConfigureAwait(false))
                .AnythingClosed;
        }
        else
        {
            _running.Report(running, "Codex");
        }

        // Invoke-CommandInDesktopPackage starts the app as a child of this PowerShell process,
        // so the env set here reaches both the app and everything it spawns (the CLI core).
        var script = BuildLaunchScript();
        var (code, output) = await powershell.RunScriptAsync(script, ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new IOException($"failed to start Codex (exit {code}): {output}");
        }

        log.Info("codex-launcher: Codex started with --proxy-server/--proxy-bypass-list and the proxy env");
        return new CodexLaunchResult(CodexLaunchOutcome.Launched, chainRaised, watch.Elapsed, restarted);
    }

    /// <summary>
    /// The already-running state of the app: a copy started outside the launcher keeps its own
    /// flags until it is closed, so the GUI and the CLI ask the owner about a restart before
    /// the launch (LauncherOptions.RestartRunning).
    /// </summary>
    public RunningAppInfo InspectRunning() => _running.Inspect();

    /// <summary>The owner has refused the restart offer: the log keeps the reason nothing started.</summary>
    public void NoteRestartDeclined() => _running.NoteDeclined("Codex");

    /// <summary>The launch script: env + package lookup + the package-context start. Testable as text.</summary>
    internal string BuildLaunchScript()
    {
        var codex = config.Codex;
        var flags = $"--proxy-server={ProxyUrl} --proxy-bypass-list={BuildBypassList()}";
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$env:HTTP_PROXY = '{Quote(ProxyUrl)}'");
        script.AppendLine($"$env:HTTPS_PROXY = '{Quote(ProxyUrl)}'");
        script.AppendLine($"$env:ALL_PROXY = '{Quote(ProxyUrl)}'");
        script.AppendLine($"$env:NO_PROXY = '{Quote(ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains))}'");
        script.AppendLine($"$pkg = Get-AppxPackage -Name '{Quote(codex.PackageName)}'");
        script.AppendLine("if (-not $pkg) { throw 'Codex desktop app is not installed for the current user (Get-AppxPackage found no " + Quote(codex.PackageName) + ")' }");
        script.AppendLine($"$exe = Join-Path $pkg.InstallLocation '{Quote(codex.AppExecutable)}'");
        script.AppendLine("Invoke-CommandInDesktopPackage -PackageFamilyName $pkg.PackageFamilyName -AppId '" + Quote(codex.AppId) + "' -Command $exe -Args '" + Quote(flags) + "'");
        script.AppendLine("Write-Output 'PNR=ok'");
        return script.ToString();

        static string Quote(string value) => value.Replace("'", "''");
    }
}
