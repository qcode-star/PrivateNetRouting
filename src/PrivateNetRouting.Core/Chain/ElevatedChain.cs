using System.Diagnostics;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Chain;

/// <summary>
/// Elevated application commands (the chain on an old tailscale or with
/// chain.daemonElevation=always; the guards): self-launch via runas (UAC).
/// </summary>
public static class ElevatedChainCommands
{
    public const string StartDaemon = "--elevated-start-daemon";
    public const string StopChain = "--elevated-stop-chain";

    /// <summary>
    /// Runs in an already elevated process (App or pnr). Returns the exit code;
    /// the result is written to a file with a nonce so the non-elevated parent waits for
    /// exactly its own launch, guarded against a stale one).
    /// </summary>
    public static int Execute(string command, string resultFile, string? configPath = null)
    {
        try
        {
            var config = AppConfigStore.LoadOrCreate(configPath);
            var log = new FileLog(
                Path.Combine(config.Logging.Directory, "chain-elevated.log"),
                config.Logging.EffectiveLevel);
            var stateDir = config.Chain.StateDir;
            Directory.CreateDirectory(stateDir);

            var detail = command switch
            {
                StartDaemon => StartDaemonCore(config, log),
                StopChain => StopChainCore(config, log),
                _ => $"unknown command: {command}",
            };
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

    private static string StartDaemonCore(AppConfig config, FileLog log)
    {
        var chain = config.Chain;
        var daemon = new SidecarDaemon(
            chain,
            new TcpPortProbe(),
            new WmiProcessFinder(),
            log,
            new ElevatedRunner(chain, log, elevatedBypass: true),
            new TailscaleVersionProbe(chain.TailscaledPath));
        if (daemon.IsHttpListening && daemon.IsProcessAlive)
        {
            return "daemon is already listening";
        }

        daemon.StartProcess(devMode: false);
        var httpPort = Socks5Client.ParseEndPoint(chain.DaemonHttpListen).Port;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(chain.DaemonWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (new TcpPortProbe().IsListening(httpPort))
            {
                log.Info("elevated: daemon listener is up");
                return "daemon started, listener is up";
            }

            Thread.Sleep(500);
        }

        var errTail = ReadTail(Path.Combine(chain.StateDir, "daemon.err.log"), lines: 15);
        log.Error("elevated: daemon did not bring up the listener");
        throw new IOException($"daemon listener did not come up within {chain.DaemonWaitSeconds}s; daemon.err.log: {errTail}");
    }

    private static string StopChainCore(AppConfig config, FileLog log)
    {
        var chain = config.Chain;
        var cli = new SidecarCli(chain);
        try
        {
            var down = cli.DownAsync(CancellationToken.None).GetAwaiter().GetResult();
            log.Info($"elevated: down -> {down.ExitCode}");
        }
        catch (Exception ex)
        {
            log.Warn($"elevated: down failed (continuing): {ex.Message}");
        }

        var killed = 0;
        foreach (var (name, pattern) in new[]
                 {
                     ("tailscaled.exe", chain.StateDir),
                     ("tailscale.exe", config.Chain.Socket),
                 })
        {
            foreach (var found in new WmiProcessFinder().Find(name, pattern))
            {
                try
                {
                    Process.GetProcessById(found.Id).Kill();
                    log.Info($"elevated: killed {name} pid={found.Id}");
                    killed++;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log.Warn($"elevated: failed to kill {name} pid={found.Id}: {ex.Message}");
                }
            }
        }

        return $"processes stopped: {killed}";
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
    ChainConfig config,
    FileLog log,
    bool elevatedBypass = false,
    Func<string, string, int>? executor = null) : IElevatedCommandRunner
{
    private readonly Func<string, string, int> _executor =
        executor ?? ((command, resultFile) => ElevatedChainCommands.Execute(command, resultFile));

    public async Task<ElevatedResult> RunAsync(string command, CancellationToken ct)
    {
        if (elevatedBypass || WindowsPrivilege.IsElevated())
        {
            var code = _executor(command, MakeResultPath(command, out _));
            var detail = code == 0 ? "executed in the current process" : "failed (see log)";
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
                Arguments = $"{command} --result-file {ProcessOps.Quote(resultPath)}",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Warn($"elevated {command}: UAC declined: {ex.Message}");
            return new ElevatedResult(false, $"UAC declined: {ex.Message}");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.UacWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resultPath))
            {
                var text = File.ReadAllText(resultPath).Trim();
                if (text.StartsWith("ok", StringComparison.Ordinal))
                {
                    File.Delete(resultPath);
                    log.Info($"elevated {command}: {text[2..].Trim()}");
                    return new ElevatedResult(true, text[2..].Trim());
                }

                if (text.StartsWith("fail", StringComparison.Ordinal))
                {
                    File.Delete(resultPath);
                    log.Error($"elevated {command}: {text[4..].Trim()}");
                    return new ElevatedResult(false, text[4..].Trim());
                }
            }

            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                return new ElevatedResult(false, "wait interrupted");
            }
        }

        log.Error($"elevated {command}: UAC/start not confirmed within {config.UacWaitSeconds}s");
        return new ElevatedResult(false, $"UAC/start not confirmed within {config.UacWaitSeconds}s");
    }

    private string MakeResultPath(string command, out string nonce)
    {
        nonce = Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(config.StateDir, $"{command.TrimStart('-')}={nonce}.result");
        // the result file must have where to appear even if the daemon (the usual creator of the
        // statedir) has never run — otherwise the helper cannot report even a failure
        Directory.CreateDirectory(config.StateDir);
        return path;
    }
}
