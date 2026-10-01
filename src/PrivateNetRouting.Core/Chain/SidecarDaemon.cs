using System.Diagnostics;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Chain;

public interface ISidecarDaemon
{
    bool IsHttpListening { get; }

    bool IsProcessAlive { get; }

    /// <summary>Идемпотентный запуск: уже жив — пропуск; протух — добить; старт и ожидание слушателя.</summary>
    Task EnsureStartedAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Убивает процессы демона (при необходимости — элевейтнуто).</summary>
    Task KillAsync(CancellationToken ct);
}

/// <summary>
/// Userspace-tailscaled (второй демон рядом с системной службой): без TUN, без маршрутов
/// и DNS в ОС. Неэлевейтнутый процесс не может создать именованный пайп — старт всегда
/// через UAC (напрямую, если мы уже с правами, иначе через элевейтнутую копию себя).
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

    public bool IsProcessAlive => processes.Find("tailscaled.exe", config.StateDir).Count > 0;

    public async Task EnsureStartedAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsHttpListening && IsProcessAlive)
        {
            log.Info("daemon: уже слушает, пропуск");
            return;
        }

        if (IsProcessAlive && !IsHttpListening)
        {
            progress?.Report("демон: протухший процесс без слушателя, добиваю");
            await KillAsync(ct);
        }

        if (IsHttpListening && !IsProcessAlive)
        {
            // чужой слушатель на нашем порту (например, прежняя схема): стартовать нельзя
            throw new IOException(
                $"порт {HttpPort} уже слушает чужой процесс (демон цепочки не найден) — см. конфиг chain.daemonHttpListen");
        }

        progress?.Report("демон: запускаю tailscaled (подтверди UAC)");
        if (WindowsPrivilege.IsElevated())
        {
            StartProcess();
        }
        else
        {
            var result = await elevated.RunAsync(ElevatedChainCommands.StartDaemon, ct);
            if (!result.Ok)
            {
                throw new IOException($"демон не поднялся: {result.Detail}");
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.DaemonWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsHttpListening)
            {
                log.Info("daemon: слушатель поднят");
                return;
            }

            await Task.Delay(500, ct);
        }

        throw new IOException(
            $"демон не поднял слушатель за {config.DaemonWaitSeconds}s; лог: {Path.Combine(config.StateDir, "daemon.err.log")}");
    }

    /// <summary>Прямой запуск процесса демона (контекст уже с правами администратора).</summary>
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
        log.Info($"daemon: запущен через cmd, pid={started?.Id}");
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
            throw new IOException($"не удалось остановить демон: {result.Detail}");
        }
    }

    private void KillProcesses()
    {
        foreach (var found in processes.Find("tailscaled.exe", config.StateDir))
        {
            try
            {
                Process.GetProcessById(found.Id).Kill();
                log.Info($"daemon: убит pid={found.Id}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn($"daemon: не удалось убить pid={found.Id}: {ex.Message}");
            }
        }
    }
}
