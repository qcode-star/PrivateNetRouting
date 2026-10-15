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

    /// <summary>Kills the daemon processes (elevated or direct, by chain.tailscale.elevation).</summary>
    Task KillAsync(CancellationToken ct);

    /// <summary>Whether starting the daemon now goes through the elevated helper (a UAC prompt is possible).</summary>
    bool StartNeedsUac();
}

/// <summary>
/// Userspace-tailscaled (a second daemon next to the system service): no TUN, no routes
/// or DNS in the OS. On tailscale 1.104.1+ the daemon pipe no longer names Administrators as
/// its owner, so a non-elevated start works with --windows-mode=dev (the pipe is owned by the
/// current user); older builds cannot create the pipe and are started through the elevated
/// runas copy. chain.tailscale.elevation picks: auto (by the daemon version) / never / always;
/// the modes are version-locked — a 1.104.1+ daemon refuses a custom socket without the dev
/// flag even elevated, and an older daemon refuses to listen without elevation.
/// </summary>
public sealed class SidecarDaemon(
    ChainTailscaleConfig config,
    IPortProbe ports,
    IProcessFinder processes,
    FileLog log,
    ElevatedRunner elevated,
    ITailscaleVersionProbe versionProbe) : ISidecarDaemon
{
    /// <summary>The first tailscale whose daemon pipe a non-elevated process can create (the SDDL owner fix).</summary>
    internal static readonly Version MinNonElevatedVersion = new(1, 104, 1);

    private int HttpPort => Socks5Client.ParseEndPoint(config.HttpListen).Port;

    public bool IsHttpListening => ports.IsListening(HttpPort);

    /// <summary>
    /// Daemon liveness = responsiveness of its named pipe. For an elevated daemon
    /// (chain.tailscale.elevation=always, or an old tailscale in auto) the command line is not visible
    /// to a non-elevated WMI reader (Windows 11 24H2+ returns an empty CommandLine), so identifying
    /// "our" processes by statedir in the cmdline does not work for status; the pipe with our name,
    /// though, can only be held by a daemon started with our --socket (the Tailscale system
    /// service has its own). A stop in the elevated mode still kills processes by searching the
    /// cmdline — from an elevated context command lines are visible.
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
        var viaHelper = NeedsElevation();
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
                $"port {HttpPort} is already held by a foreign process (chain daemon not found) — see chain.tailscale.httpListen");
        }

        progress?.Report(viaHelper ? "daemon: starting tailscaled (confirm UAC)" : "daemon: starting tailscaled");
        if (viaHelper)
        {
            var result = await elevated.RunAsync(ElevatedChainCommands.StartDaemon, ct);
            if (!result.Ok)
            {
                throw new IOException($"daemon failed to start: {result.Detail}");
            }
        }
        else
        {
            StartProcess(devMode: true);
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
            $"daemon listener did not come up within {config.DaemonWaitSeconds}s; log: {Path.Combine(config.StateDir, "daemon.err.log")}"
            + " (a non-elevated start needs tailscale 1.104.1+ — see chain.tailscale.elevation)");
    }

    /// <summary>
    /// Direct launch of the daemon process (the context already has the rights it needs).
    /// devMode adds --windows-mode=dev — the non-elevated listen of tailscale 1.104.1+; older
    /// builds do not know the flag, their elevated start goes without it.
    /// </summary>
    public void StartProcess(bool devMode)
    {
        Directory.CreateDirectory(config.StateDir);
        var arguments = new List<string> { "--tun=userspace-networking" };
        if (devMode)
        {
            arguments.Add("--windows-mode=dev");
        }

        arguments.Add(ProcessOps.Quote($"--statedir={config.StateDir}"));
        arguments.Add(ProcessOps.Quote($"--socket={config.Socket}"));
        arguments.Add("--port=0");
        arguments.Add($"--socks5-server={config.SocksListen}");
        arguments.Add($"--outbound-http-proxy-listen={config.HttpListen}");
        var commandLine = $"{ProcessOps.Quote(config.TailscaledPath)} {string.Join(' ', arguments)}";
        var started = ProcessOps.StartViaCmd(
            commandLine,
            stdoutPath: Path.Combine(config.StateDir, "daemon.out.log"),
            stderrPath: Path.Combine(config.StateDir, "daemon.err.log"));
        log.Info($"daemon: started via cmd, pid={started?.Id}");
    }

    public async Task KillAsync(CancellationToken ct)
    {
        if (NeedsElevation())
        {
            var result = await elevated.RunAsync(ElevatedChainCommands.StopChain, ct);
            if (!result.Ok)
            {
                throw new IOException($"failed to stop daemon: {result.Detail}");
            }

            return;
        }

        KillProcesses();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && (IsProcessAlive || IsHttpListening))
        {
            await Task.Delay(200, ct);
        }

        if (IsProcessAlive || IsHttpListening)
        {
            throw new IOException(
                "daemon is still alive after a direct kill — it is probably elevated (left from a previous elevated run); stop it once from an elevated context (e.g. \"pnr chain down\" in an admin console)");
        }
    }

    /// <summary>
    /// The start goes through the runas helper (a UAC prompt) — the same decision
    /// EnsureStartedAsync makes; a second cheap version probe instead of shared state.
    /// </summary>
    public bool StartNeedsUac() => NeedsElevation();

    /// <summary>The effective decision: start/stop through the elevated runas helper?</summary>
    private bool NeedsElevation() => config.EffectiveElevation switch
    {
        DaemonElevationMode.Never => false,
        DaemonElevationMode.Always => true,
        _ => VersionNeedsElevation(versionProbe.GetVersion()),
    };

    /// <summary>The auto rule: an unknown or older-than-1.104.1 daemon goes through elevation.</summary>
    internal static bool VersionNeedsElevation(Version? version) =>
        version is null || version < MinNonElevatedVersion;

    private void KillProcesses()
    {
        foreach (var found in processes.FindByArgument("tailscaled.exe", config.StateDir))
        {
            try
            {
                if (!processes.StillCarriesArgument(found, config.StateDir))
                {
                    log.Debug($"daemon: pid={found.Id} is gone or no longer ours, not killed");
                    continue;
                }

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
