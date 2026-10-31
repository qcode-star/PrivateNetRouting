using System.Diagnostics;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Chain;

/// <summary>
/// Elevated application commands (the chain on an old tailscale or with
/// chain.tailscale.elevation=always; the guards): self-launch via runas (UAC).
/// </summary>
public static class ElevatedChainCommands
{
    public const string StartDaemon = "--elevated-start-daemon";
    public const string StopChain = "--elevated-stop-chain";

    /// <summary>
    /// Runs in an already elevated process (App or pnr). Returns the exit code;
    /// the result is written to a file with a nonce so the non-elevated parent waits for
    /// exactly its own launch, guarded against a stale one). <paramref name="profileEnvironment"/> —
    /// the raw `--env` entries of the asking user's profile variables (see ElevatedHelperArguments).
    /// </summary>
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

            var config = AppConfigStore.LoadOrCreate(configPath, ElevatedHelperArguments.ToProfileEnvironment(profileEnvironment));
            log = ElevatedHelperTrust.OpenPinnedLog(config, "chain-elevated.log");
            var stateDir = config.Chain.Tailscale.StateDir;
            if (!ElevatedHelperTrust.IsValidStateDir(stateDir))
            {
                throw new InvalidOperationException(
                    $"chain.tailscale.stateDir '{stateDir}' is not an absolute local path free of cmd.exe metacharacters — refusing to create it as administrator");
            }

            Directory.CreateDirectory(stateDir);

            var detail = command switch
            {
                StartDaemon => StartDaemonCore(config, log, resultFile),
                StopChain => StopChainCore(config, log),
                _ => $"unknown command: {command}",
            };
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

    /// <summary>
    /// The daemon start executes the chain binaries as administrator, so the user-writable config
    /// does not name them: like the guard half, the helper pins them to the standard per-machine
    /// Tailscale install (logged when they differed) and refuses the start when that install is
    /// missing. The stop executes nothing but an optional `tailscale down` (see TryPinCli).
    /// </summary>
    internal static void PinChainLocations(AppConfig config, FileLog log, string? tailscaled = null, string? cli = null)
    {
        tailscaled ??= ElevatedHelperTrust.SystemTailscaled();
        cli ??= ElevatedHelperTrust.SystemTailscaleCli();
        if (!File.Exists(tailscaled) || !File.Exists(cli))
        {
            throw new FileNotFoundException(
                $"the elevated chain helper executes only the standard Tailscale install ({tailscaled}); it is missing, refusing to run");
        }

        if (!string.Equals(Path.GetFullPath(config.Chain.Tailscale.TailscaledPath), tailscaled, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"elevated helper: chain.tailscale.tailscaledPath '{config.Chain.Tailscale.TailscaledPath}' ignored, using the standard Tailscale install");
        }

        if (!string.Equals(Path.GetFullPath(config.Chain.Tailscale.CliPath), cli, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"elevated helper: chain.tailscale.cliPath '{config.Chain.Tailscale.CliPath}' ignored, using the standard Tailscale install");
        }

        config.Chain.Tailscale.TailscaledPath = tailscaled;
        config.Chain.Tailscale.CliPath = cli;
    }

    /// <summary>
    /// The stop's only executed binary is the CLI of its `tailscale down` step: pinned to the
    /// standard install when that CLI is there, otherwise the step is skipped (false) — the kill
    /// by image name and statedir/socket executes nothing, so a non-standard install can still be
    /// taken down.
    /// </summary>
    internal static bool TryPinCli(AppConfig config, FileLog log, string? cli = null)
    {
        cli ??= ElevatedHelperTrust.SystemTailscaleCli();
        if (!File.Exists(cli))
        {
            log.Warn($"elevated helper: the standard Tailscale CLI ({cli}) is missing, the node is not downed — the processes are stopped by the kill alone");
            return false;
        }

        if (!string.Equals(Path.GetFullPath(config.Chain.Tailscale.CliPath), cli, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"elevated helper: chain.tailscale.cliPath '{config.Chain.Tailscale.CliPath}' ignored, using the standard Tailscale install");
        }

        config.Chain.Tailscale.CliPath = cli;
        return true;
    }

    /// <summary>
    /// The dev flag of the elevated start follows the version of the binary the helper actually
    /// executes (the pinned one), not the one the asking side probed: a 1.104.1+ daemon refuses a
    /// custom socket without --windows-mode=dev even elevated, an older one does not know the flag;
    /// an unknown version starts without it.
    /// </summary>
    internal static bool ElevatedDevMode(Version? pinnedVersion) => !SidecarDaemon.VersionNeedsElevation(pinnedVersion);

    private static string StartDaemonCore(AppConfig config, FileLog log, string resultFile)
    {
        // the asking side may have cancelled the bring-up while this helper was on its way (the
        // UAC prompt was confirmed before the cancel): starting the daemon now would undo the
        // stop that already ran during the cancellation — abort instead
        if (File.Exists(resultFile + ElevatedRunner.CancelSuffix))
        {
            log.Info("elevated: the bring-up was cancelled by the asking side — the daemon is not started");
            return "cancelled before the start";
        }

        PinChainLocations(config, log);
        var chain = config.Chain;
        var daemon = new SidecarDaemon(
            chain.Tailscale,
            new TcpPortProbe(),
            new WmiProcessFinder(),
            log,
            new ElevatedRunner(chain.Tailscale, log, elevatedBypass: true),
            new TailscaleVersionProbe(chain.Tailscale.TailscaledPath));
        if (daemon.IsHttpListening && daemon.IsProcessAlive)
        {
            return "daemon is already listening";
        }

        var version = new TailscaleVersionProbe(chain.Tailscale.TailscaledPath).GetVersion();
        var devMode = ElevatedDevMode(version);
        log.Info($"elevated: starting the standard tailscaled {version?.ToString() ?? "(unknown version)"}{(devMode ? " with --windows-mode=dev" : "")}");
        daemon.StartProcess(devMode);
        var httpPort = Socks5Client.ParseEndPoint(chain.Tailscale.HttpListen).Port;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(chain.Tailscale.DaemonWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (new TcpPortProbe().IsListening(httpPort))
            {
                log.Info("elevated: daemon listener is up");
                return "daemon started, listener is up";
            }

            // a cancel that landed mid-start must not leave a daemon behind: what this helper
            // started, this helper takes down (it is already elevated)
            if (File.Exists(resultFile + ElevatedRunner.CancelSuffix))
            {
                log.Warn("elevated: the bring-up was cancelled mid-start — stopping the daemon it started");
                daemon.KillAsync(CancellationToken.None).GetAwaiter().GetResult();
                throw new IOException("the bring-up was cancelled mid-start; the daemon it started was stopped");
            }

            Thread.Sleep(500);
        }

        var errTail = ReadTail(Path.Combine(chain.Tailscale.StateDir, "daemon.err.log"), lines: 15);
        log.Error("elevated: daemon did not bring up the listener");
        throw new IOException($"daemon listener did not come up within {chain.Tailscale.DaemonWaitSeconds}s; daemon.err.log: {errTail}");
    }

    private static string StopChainCore(AppConfig config, FileLog log)
    {
        var chain = config.Chain;
        if (TryPinCli(config, log))
        {
            var cli = new SidecarCli(chain.Tailscale);
            try
            {
                var down = cli.DownAsync(CancellationToken.None).GetAwaiter().GetResult();
                log.Info($"elevated: down -> {down.ExitCode}");
            }
            catch (Exception ex)
            {
                log.Warn($"elevated: down failed (continuing): {ex.Message}");
            }
        }

        var killed = 0;
        foreach (var (name, pattern) in new[]
                 {
                     ("tailscaled.exe", chain.Tailscale.StateDir),
                     ("tailscale.exe", chain.Tailscale.Socket),
                 })
        {
            var finder = new WmiProcessFinder();
            foreach (var found in finder.FindByArgument(name, pattern))
            {
                try
                {
                    if (!finder.KillIfStillCarries(found, pattern))
                    {
                        log.Debug($"elevated: {name} pid={found.Id} is gone or no longer ours, not killed");
                        continue;
                    }

                    log.Info($"elevated: killed {name} pid={found.Id}");
                    killed++;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log.Warn($"elevated: failed to kill {name} pid={found.Id}: {ex.Message}");
                }
            }
        }

        // the kill count alone proves nothing (a wrong statedir, a WMI blind spot): the stop is
        // only done when nothing of ours answers anymore — the pipe is the daemon's liveness
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && IsStillThere(chain))
        {
            Thread.Sleep(200);
        }

        if (IsStillThere(chain))
        {
            var detail = $"the daemon is still there after the stop (the pipe {chain.Tailscale.Socket} answers or a tailscaled.exe with the statedir is running)";
            log.Error($"elevated stop: {detail}");
            throw new IOException(detail);
        }

        return $"processes stopped: {killed}";
    }

    /// <summary>Whether the daemon survived the stop: a responsive pipe, or a tailscaled with our statedir.</summary>
    private static bool IsStillThere(ChainConfig chain)
    {
        if (SidecarDaemon.ProbePipe(chain.Tailscale.Socket, TimeSpan.FromMilliseconds(300)))
        {
            return true;
        }

        return new WmiProcessFinder().FindByArgument("tailscaled.exe", chain.Tailscale.StateDir).Count > 0;
    }

    private static string ReadTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "(no file)";
            }

            var tail = File.ReadLines(path).TakeLast(lines);
            return string.Join(" | ", tail).TrimEnd();
        }
        catch (IOException)
        {
            return "(unreadable)";
        }
    }
}

public sealed record ElevatedResult(bool Ok, string Detail);

/// <summary>
/// Launches an elevated application command (chain, guards): self via runas (UAC) waiting for
/// the result file, or — if the process already has rights — execution in the current process.
/// </summary>
public interface IElevatedCommandRunner
{
    Task<ElevatedResult> RunAsync(string command, CancellationToken ct);
}

/// <summary>
/// Launches the application's own exe elevated (runas, UAC) and waits for the result file
/// with a nonce. In an elevated process (elevatedBypass) it starts without UAC — already with rights.
/// executor — what exactly to execute in the bypass process (chain or guard commands).
/// </summary>
public sealed class ElevatedRunner(
    ChainTailscaleConfig config,
    FileLog log,
    bool elevatedBypass = false,
    Func<string, string, int>? executor = null,
    string? configPath = null) : IElevatedCommandRunner
{
    /// <summary>Results older than this are leftovers of a helper that outlived its parent's wait.</summary>
    private static readonly TimeSpan StaleResultAge = TimeSpan.FromDays(1);

    private readonly Func<string, string, int> _executor =
        executor ?? ((command, resultFile) => ElevatedChainCommands.Execute(command, resultFile));

    /// <summary>The config this process runs with — handed to the helper, which may run as another account.</summary>
    private readonly string _configPath = configPath ?? AppConfigStore.GetDefaultConfigPath();

    public async Task<ElevatedResult> RunAsync(string command, CancellationToken ct)
    {
        if (elevatedBypass || WindowsPrivilege.IsElevated())
        {
            ct.ThrowIfCancellationRequested();
            var bypassResult = MakeResultPath(command, out _);
            // off the caller's thread: the command polls with sleeps and waits on the sidecar CLI,
            // and the caller may be the UI thread of an elevated GUI
            var code = await Task.Run(() => _executor(command, bypassResult), CancellationToken.None).ConfigureAwait(false);
            // the helper reports the detail through the same result file; what it wrote is what is shown
            var reported = ReadResult(bypassResult);
            DeleteQuietly(bypassResult);
            var detail = reported is not null && reported.Detail.Length > 0
                ? reported.Detail
                : code == 0 ? "executed in the current process" : "failed (see log)";
            if (code == 0)
            {
                log.Info($"elevated {command}: {detail}");
            }
            else
            {
                log.Error($"elevated {command}: {detail}");
            }

            return new ElevatedResult(code == 0, detail);
        }

        // no UAC prompt for an operation that is already cancelled
        ct.ThrowIfCancellationRequested();

        var resultPath = MakeResultPath(command, out var nonce);
        _ = nonce; // uniqueness is guaranteed by the file name
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("failed to determine own executable path");

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = ElevatedHelperArguments.Build(
                    command, resultPath, _configPath, ElevatedHelperArguments.CurrentProfileEnvironment()),
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Warn($"elevated {command}: UAC declined: {ex.Message}");
            return new ElevatedResult(false, $"UAC declined: {ex.Message}");
        }

        var result = await WaitForResultAsync(
            resultPath,
            TimeSpan.FromSeconds(config.UacWaitSeconds),
            ct,
            () => MarkCancelled(resultPath)).ConfigureAwait(false);
        if (result is not null)
        {
            DeleteQuietly(resultPath);
            DeleteQuietly(resultPath + CancelSuffix);
            if (result.Ok)
            {
                log.Info($"elevated {command}: {result.Detail}");
            }
            else
            {
                log.Error($"elevated {command}: {result.Detail}");
            }

            return result;
        }

        log.Error($"elevated {command}: UAC/start not confirmed within {config.UacWaitSeconds}s");
        return new ElevatedResult(false, $"UAC/start not confirmed within {config.UacWaitSeconds}s");
    }

    /// <summary>Suffix of a launch's cancellation marker: the helper aborts before starting the daemon once it appears.</summary>
    internal const string CancelSuffix = ".cancel";

    /// <summary>
    /// Waits for the helper's result file. A cancellation surfaces as OperationCanceledException
    /// (the chain contract), not as a failure of the helper — and marks the launch cancelled on
    /// the way out: the helper may still be on its way (the UAC prompt was confirmed before the
    /// cancel), and a daemon it starts after a stop already ran for would be left unwatched.
    /// </summary>
    internal static async Task<ElevatedResult?> WaitForResultAsync(
        string resultPath, TimeSpan wait, CancellationToken ct, Action? onCancelled = null)
    {
        var deadline = DateTime.UtcNow + wait;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (ReadResult(resultPath) is { } result)
                {
                    return result;
                }

                await Task.Delay(500, ct);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            onCancelled?.Invoke();
            throw;
        }
    }

    /// <summary>Marks a launch cancelled; the helper aborts its start when it sees the marker.</summary>
    internal static void MarkCancelled(string resultPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath + CancelSuffix, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing to mark: the helper starts unimpeded and its outcome lands in the result file
        }
    }

    /// <summary>
    /// The result the helper wrote; null - not there yet, unfinished, or the file is momentarily
    /// held by the writer (the wait goes on instead of failing on a sharing violation).
    /// </summary>
    internal static ElevatedResult? ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            if (text.StartsWith("ok", StringComparison.Ordinal))
            {
                return new ElevatedResult(true, text[2..].Trim());
            }

            return text.StartsWith("fail", StringComparison.Ordinal)
                ? new ElevatedResult(false, text[4..].Trim())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover is swept with the stale ones at the next run
        }
    }

    private string MakeResultPath(string command, out string nonce)
    {
        nonce = Guid.NewGuid().ToString("N")[..8];
        // the result travels in the pinned elevated directory: the helper (an administrator)
        // refuses to write anywhere else, so the asking side places it there in the first place;
        // the directory must exist even if the helper never runs — otherwise nothing can report
        var path = Path.Combine(ElevatedHelperTrust.PinnedDirectory, $"{command.TrimStart('-')}={nonce}.result");
        Directory.CreateDirectory(ElevatedHelperTrust.PinnedDirectory);
        SweepStaleResults();
        return path;
    }

    /// <summary>Result files and cancellation markers of earlier runs that nobody collected (a timed-out wait, a failed delete).</summary>
    private void SweepStaleResults()
    {
        try
        {
            var leftovers = Directory.EnumerateFiles(ElevatedHelperTrust.PinnedDirectory, "*.result")
                .Concat(Directory.EnumerateFiles(ElevatedHelperTrust.PinnedDirectory, "*" + CancelSuffix));
            foreach (var file in leftovers)
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleResultAge)
                {
                    DeleteQuietly(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // housekeeping only
        }
    }
}
