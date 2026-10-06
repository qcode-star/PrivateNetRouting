using System.Diagnostics;
using System.IO.Pipes;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Chain;

public interface ISidecarDaemon
{
    bool IsHttpListening { get; }

    bool IsProcessAlive { get; }

    /// <summary>Idempotent start: already alive — skip; stale — kill; start and wait for the listener.</summary>
    Task EnsureStartedAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Kills the daemon processes (elevated if necessary).</summary>
    Task KillAsync(CancellationToken ct);
}

/// <summary>
/// Userspace-tailscaled (a second daemon next to the system service): no TUN, no routes
/// or DNS in the OS. A non-elevated process cannot create a named pipe — the start always
/// goes through UAC (directly if we already have rights, otherwise via an elevated copy of ourselves).
/// </summary>
public sealed class SidecarDaemon(
    ChainConfig config,
    IPortProbe ports,
    IProcessFinder processes,
    FileLog log,
    ElevatedRunner elevated) : ISidecarDaemon
{
    private int HttpPort => Socks5Client.ParseEndPoint(config.DaemonHttpListen).Port;

    public bool IsHttpListening => ports.IsListening(HttpPort);

    /// <summary>
    /// Daemon liveness = responsiveness of its named pipe. The command line of an elevated
    /// daemon is not visible to a non-elevated WMI reader (Windows 11 24H2+ returns an empty CommandLine),
    /// so identifying "our" processes by statedir in the cmdline does not work for status; the pipe with our
    /// name, though, can only be held by a daemon started with our --socket (the Tailscale system
    /// service has its own). An elevated shutdown still kills processes by searching the cmdline —
    /// from an elevated context command lines are visible.
    /// </summary>
    public bool IsProcessAlive => ProbePipe(config.Socket, TimeSpan.FromMilliseconds(500));

    internal static bool ProbePipe(string socketPath, TimeSpan timeout)
    {
        const string prefix = @"\\.\pipe\";
        if (!socketPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", socketPath[prefix.Length..], PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ArgumentException)
        {
            return false;
        }
    }

    public async Task EnsureStartedAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsHttpListening && IsProcessAlive)
        {
            log.Debug("daemon: already listening, skipping");
            return;
        }

        if (IsProcessAlive && !IsHttpListening)
        {
            progress?.Report("daemon: stale process without listener, killing it");
            await KillAsync(ct);
        }

        if (IsHttpListening && !IsProcessAlive)
        {
            // a foreign listener on our port: we must not start
            throw new IOException(
                $"port {HttpPort} is already held by a foreign process (chain daemon not found) — see chain.daemonHttpListen");
        }

        progress?.Report("daemon: starting tailscaled (confirm UAC)");
        if (WindowsPrivilege.IsElevated())
        {
            StartProcess();
        }
        else
        {
            var result = await elevated.RunAsync(ElevatedChainCommands.StartDaemon, ct);
            if (!result.Ok)
            {
                throw new IOException($"daemon failed to start: {result.Detail}");
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.DaemonWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsHttpListening)
            {
                log.Info("daemon: listener is up");
                return;
            }

            await Task.Delay(500, ct);
        }

        throw new IOException(
            $"daemon listener did not come up within {config.DaemonWaitSeconds}s; log: {Path.Combine(config.StateDir, "daemon.err.log")}");
    }

    /// <summary>Direct launch of the daemon process (the context already has administrator rights).</summary>
    public void StartProcess()
    {
        Directory.CreateDirectory(config.StateDir);
        var commandLine = string.Join(' ',
            ProcessOps.Quote(config.TailscaledPath),
            "--tun=userspace-networking",
            ProcessOps.Quote($"--statedir={config.StateDir}"),
            ProcessOps.Quote($"--socket={config.Socket}"),
            "--port=0",
            $"--socks5-server={config.DaemonSocksListen}",
            $"--outbound-http-proxy-listen={config.DaemonHttpListen}");
        var started = ProcessOps.StartViaCmd(
            commandLine,
            stdoutPath: Path.Combine(config.StateDir, "daemon.out.log"),
            stderrPath: Path.Combine(config.StateDir, "daemon.err.log"));
        log.Info($"daemon: started via cmd, pid={started?.Id}");
    }

    public async Task KillAsync(CancellationToken ct)
    {
        if (WindowsPrivilege.IsElevated())
        {
            KillProcesses();
            return;
        }

        var result = await elevated.RunAsync(ElevatedChainCommands.StopChain, ct);
        if (!result.Ok)
        {
            throw new IOException($"failed to stop daemon: {result.Detail}");
        }
    }

    private void KillProcesses()
    {
        foreach (var found in processes.Find("tailscaled.exe", config.StateDir))
        {
            try
            {
                Process.GetProcessById(found.Id).Kill();
                log.Info($"daemon: killed pid={found.Id}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn($"daemon: failed to kill pid={found.Id}: {ex.Message}");
            }
        }
    }
}
