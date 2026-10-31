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
    /// <summary>Consecutive failed chain stops after which the stopper gives up and exits.</summary>
    internal const int MaxFailedChainStops = 3;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task RunAsync(IProgress<ChainStep>? progress, CancellationToken ct)
    {
        log.Info($"watchdog: started (interval {config.CheckSeconds}s)");
        var idleWindow = TimeSpan.FromSeconds(config.CheckSeconds);
        string? previousReason = null;
        var failedStops = 0;
        while (true)
        {
            // ConfigureAwait: the worker host blocks a thread on this call (App OnStartup) — the
            // continuations must not land in its context (the Core start/stop rule)
            await _delay(idleWindow, ct).ConfigureAwait(false);
            try
            {
                var outcome = await TickAsync(idleWindow, previousReason, progress, ct).ConfigureAwait(false);
                if (outcome.Done)
                {
                    return;
                }

                // regained activity: a later idle stop starts its failure count afresh
                failedStops = 0;
                previousReason = outcome.Reason;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ChainStopFailedException ex)
            {
                // a stop that keeps failing (a declined UAC, nobody at the machine) must not raise a
                // prompt and a full WSL pass every interval forever: after a few tries the stopper gives up
                failedStops++;
                if (failedStops >= MaxFailedChainStops)
                {
                    log.Error($"watchdog: the chain stop failed {failedStops} times in a row, giving up: {ex.InnerException!.Message}");
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }

                TryWarn($"watchdog: chain stop failed ({failedStops} of {MaxFailedChainStops}), will retry in {config.CheckSeconds}s: {ex.InnerException!.Message}");
            }
            catch (Exception ex)
            {
                // one failed tick (a probe or log I/O error) must
                // not end the stopper: it would leave the chain up with nobody watching — retry next tick
                TryWarn($"watchdog: tick failed, will retry in {config.CheckSeconds}s: {ex.Message}");
            }
        }
    }

    private async Task<TickOutcome> TickAsync(
        TimeSpan idleWindow,
        string? previousReason,
        IProgress<ChainStep>? progress,
        CancellationToken ct)
    {
        var reason = usage.GetInUseReason(idleWindow);
        if (reason is not null)
        {
            log.Debug($"watchdog: in use — {reason}");
            if (reason != previousReason)
            {
                log.Info($"watchdog: in use — {reason}");
            }

            return new TickOutcome(false, reason);
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

            // the WSL pass takes up to minutes: a client started meanwhile (a shortcut click finds
            // the chain live and raises nothing) must not lose its chain to a verdict taken before
            var resumed = usage.GetInUseReason(idleWindow);
            if (resumed is not null)
            {
                log.Info($"watchdog: in use again before the chain stop — {resumed}");
                return new TickOutcome(false, resumed);
            }
        }

        try
        {
            await chain.StopAsync(progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new ChainStopFailedException(ex);
        }

        log.Info("watchdog: chain stopped, exiting");
        return new TickOutcome(true, null);
    }

    private void TryWarn(string message)
    {
        try
        {
            log.Warn(message);
        }
        catch (Exception)
        {
            // the log itself is what failed: nothing left to report to
        }
    }

    private sealed record TickOutcome(bool Done, string? Reason);

    /// <summary>Marks a failure of the chain stop itself, counted apart from other tick failures.</summary>
    private sealed class ChainStopFailedException(Exception inner) : Exception(inner.Message, inner);
}
