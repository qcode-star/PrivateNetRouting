using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>
/// Супервизор автостопа по простою (порт ts-sidecar-watch-claude.ps1): раз в checkSeconds
/// проверяет занятость Claude; при простое сначала гасит WSL-хост (его процессы не видны
/// как Windows-процессы и иначе висят в дистрибутиве до следующего запуска Desktop), затем
/// останавливает цепочку и завершается — следующий запуск лончера поднимет всё заново.
/// Первый опрос — через интервал после старта; каждое срабатывание таймера пишется в Debug,
/// в Info — только смена причины занятости и сам автостоп. Останов цепочки — существующим
/// оркестратором (убийство демона при необходимости поднимет UAC). Запускается только явным
/// действием (CLI/переключатель приложения); в автозапуск не регистрируется (правило №4 AGENTS.md).
/// </summary>
public sealed class WatchdogService(
    WatchdogConfig config,
    IClaudeUsageProbe usage,
    IWslClaudeHost? wslHost,
    IChainSupervisor chain,
    FileLog log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task RunAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        log.Info($"watchdog: started (interval {config.CheckSeconds}s)");
        var idleWindow = TimeSpan.FromSeconds(config.CheckSeconds);
        string? previousReason = null;
        while (true)
        {
            await _delay(idleWindow, ct);
            var reason = usage.GetInUseReason(idleWindow);
            if (reason is not null)
            {
                log.Debug($"watchdog: in use — {reason}");
                if (reason != previousReason)
                {
                    log.Info($"watchdog: in use — {reason}");
                }

                previousReason = reason;
                continue;
            }

            log.Info("watchdog: idle — stopping");
            if (wslHost is null)
            {
                log.Info("watchdog: WSL distro is not set, the WSL host is not stopped");
            }
            else
            {
                try
                {
                    var result = await wslHost.StopAsync(ct);
                    log.Info($"watchdog: wsl host: {result.State} {result.Detail}");
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                    log.Warn($"watchdog: WSL host stop failed: {ex.Message}");
                }
            }

            await chain.StopAsync(progress, ct);
            log.Info("watchdog: chain stopped, exiting");
            return;
        }
    }
}
