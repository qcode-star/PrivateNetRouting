using System.Diagnostics;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Chain;

public interface ISidecarKeeper
{
    bool IsRunning { get; }

    /// <summary>Старт кипера, если его нет; сразу вылетевший процесс = ошибка.</summary>
    Task EnsureStartedAsync(CancellationToken ct);

    Task KillAsync(CancellationToken ct);
}

/// <summary>
/// Кипер watch-ipn: Windows userspace-tailscaled гасит движок при отключении последнего
/// локального ipn-клиента и сбрасывает WantRunning — держим перманентного подписчика шины.
/// stdout (поток netmap-JSON непрерывно) уходит в NUL, stderr — в watch.err.log.
/// </summary>
public sealed class SidecarKeeper(ChainConfig config, IProcessFinder processes, FileLog log) : ISidecarKeeper
{
    public bool IsRunning =>
        processes.Find("tailscale.exe", config.Socket)
            .Any(p => p.CommandLine.Contains("watch-ipn", StringComparison.OrdinalIgnoreCase));

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsRunning)
        {
            log.Info("keeper: уже работает, пропуск");
            return;
        }

        Directory.CreateDirectory(config.StateDir);
        var commandLine = string.Join(' ',
            ProcessOps.Quote(config.TailscaleCliPath),
            ProcessOps.Quote($"--socket={config.Socket}"),
            "debug", "watch-ipn");
        var started = ProcessOps.StartViaCmd(
            commandLine,
            stdoutPath: null, // NUL: netmap-JSON не нужен никому
            stderrPath: Path.Combine(config.StateDir, "watch.err.log"));
        log.Info($"keeper: запущен через cmd, pid={started?.Id}");

        await Task.Delay(TimeSpan.FromSeconds(config.KeeperCheckSeconds), ct);
        if (!IsRunning)
        {
            throw new IOException(
                $"кипер watch-ipn вылетел сразу; лог: {Path.Combine(config.StateDir, "watch.err.log")}");
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
                log.Info($"keeper: убит pid={found.Id}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn($"keeper: не удалось убить pid={found.Id}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }
}
