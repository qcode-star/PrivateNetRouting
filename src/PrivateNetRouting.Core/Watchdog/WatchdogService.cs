using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Watchdog;

/// <summary>
/// Idle auto-stop supervisor: every checkSeconds
/// it checks whether Claude is busy; when idle it first stops the app hosts inside the RUNNING
/// configured WSL distros (their processes are not visible as Windows processes and would
/// otherwise linger in the distro until the next Desktop start), then stops the chain and exits —
/// the next launcher start brings everything back up.
/// The first poll happens one interval after startup; every timer firing is logged at Debug,
/// at Info — only a change of the busy reason and the auto-stop itself. The chain stop goes through
/// the existing orchestrator (killing the daemon raises UAC if needed). It is started only by an explicit
/// action (CLI/application toggle); it is never registered for autostart (rule #4 of AGENTS.md).
/// </summary>
public sealed class WatchdogService(
    WatchdogConfig config,
    IClaudeUsageProbe usage,
    IWslAppHost? wslHost,
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
            // ConfigureAwait: the worker host blocks a thread on this call (App OnStartup) — the
            // continuations must not land in its context (the Core start/stop rule)
            await _delay(idleWindow, ct).ConfigureAwait(false);
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
                log.Info("watchdog: no distros configured (wsl.distros), the WSL step is skipped");
            }
            else
            {
                try
                {
                    var result = await wslHost.StopAsync(ct).ConfigureAwait(false);
                    log.Info($"watchdog: wsl host: {result.State} {result.Detail}");
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                    log.Warn($"watchdog: WSL host stop failed: {ex.Message}");
                }
            }

            await chain.StopAsync(progress, ct).ConfigureAwait(false);
            log.Info("watchdog: chain stopped, exiting");
            return;
        }
    }
}
