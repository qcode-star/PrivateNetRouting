using System.Diagnostics;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Chain;

public interface ISidecarKeeper
{
    bool IsRunning { get; }

    /// <summary>Start the keeper if it is absent; a process that exits immediately = an error.</summary>
    Task EnsureStartedAsync(CancellationToken ct);

    Task KillAsync(CancellationToken ct);
}

/// <summary>
/// watch-ipn keeper: Windows userspace-tailscaled shuts down the engine when the last
/// local ipn client disconnects and resets WantRunning — so we keep a permanent bus subscriber.
/// stdout (a continuous netmap-JSON stream) goes to NUL, stderr — to watch.err.log.
/// </summary>
public sealed class SidecarKeeper(
    ChainTailscaleConfig config, IProcessFinder processes, FileLog log) : ISidecarKeeper
{
    public bool IsRunning =>
        processes.Find("tailscale.exe", config.Socket)
            .Any(p => p.CommandLine.Contains("watch-ipn", StringComparison.OrdinalIgnoreCase));

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsRunning)
        {
            log.Debug("keeper: already running, skipping");
            return;
        }

        Directory.CreateDirectory(config.StateDir);
        var commandLine = string.Join(' ',
            ProcessOps.Quote(config.CliPath),
            ProcessOps.Quote($"--socket={config.Socket}"),
            "debug", "watch-ipn");
        var started = ProcessOps.StartViaCmd(
            commandLine,
            stdoutPath: null, // NUL: nobody needs the netmap-JSON
            stderrPath: Path.Combine(config.StateDir, "watch.err.log"));
        log.Info($"keeper: started via cmd, pid={started?.Id}");

        await Task.Delay(TimeSpan.FromSeconds(config.KeeperCheckSeconds), ct);
        if (!IsRunning)
        {
            throw new IOException(
                $"watch-ipn keeper exited immediately; log: {Path.Combine(config.StateDir, "watch.err.log")}");
        }
    }

    public Task KillAsync(CancellationToken ct)
    {
        foreach (var found in processes.Find("tailscale.exe", config.Socket)
                     .Where(p => p.CommandLine.Contains("watch-ipn", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Process.GetProcessById(found.Id).Kill();
                log.Info($"keeper: killed pid={found.Id}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn($"keeper: failed to kill pid={found.Id}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }
}
