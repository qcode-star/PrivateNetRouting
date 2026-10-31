using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Updater;

/// <summary>Result of the Codex update check: what is installed, whether the Store offers more,
/// and the offered version when OpenAI's manifest answered (it rides the live proxy chain only).</summary>
public sealed record CodexUpdateCheck(
    InstalledPackage Installed,
    bool UpdateAvailable,
    bool StoreUnavailable,
    string? OfferedVersion = null);

/// <summary>
/// Codex desktop app update through the Microsoft Store: the check asks the Store catalog
/// (no chain, no UAC, the Store CDN is not part of the ban), the update hands the package
/// family name to the Store and polls the install item until it completes. The desktop app
/// is closed first — a running app keeps the Store from registering the new package.
/// The Store answer carries no version string: the offered version is named by OpenAI's own
/// manifest (reachable only through the proxy chain), and its absence never hides an update.
/// </summary>
public sealed class CodexUpdater(
    AppConfig config,
    IAppxPackageSource appx,
    IStoreUpdateSource store,
    IClaudeProcessSource processes,
    ICodexVersionManifest versionManifest,
    FileLog log)
{
    /// <summary>Status poll period while the Store moves the update along.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Codex updater log.</summary>
    public FileLog Log => log;

    public bool IsCodexRunning() => processes.Find(config.Codex.ProcessName).Count > 0;

    public async Task<CodexUpdateCheck> CheckAsync(CancellationToken ct)
    {
        var installed = await appx.GetInstalledAsync(config.Codex.PackageName, ct).ConfigureAwait(false)
            ?? throw new IOException(
                $"Codex desktop app is not installed as an MSIX package ({config.Codex.PackageName})");
        try
        {
            var available = await store.HasUpdateAsync(config.Codex.Update.StoreProductId, ct).ConfigureAwait(false);
            if (available)
            {
                var offered = await versionManifest.FindOfferedVersionAsync(ct).ConfigureAwait(false);
                log.Info($"codex-updater: update available (installed {installed.Version}, offered {offered ?? "?"})");
                return new CodexUpdateCheck(installed, UpdateAvailable: true, StoreUnavailable: false, offered);
            }

            log.Info($"codex-updater: installed {installed.Version} is up to date");
            return new CodexUpdateCheck(installed, UpdateAvailable: false, StoreUnavailable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"codex-updater: Store update search failed: {ex.Message}");
            return new CodexUpdateCheck(installed, UpdateAvailable: false, StoreUnavailable: true);
        }
    }

    /// <summary>
    /// Full update: close the app, queue the Store update, poll until the Store reports the
    /// new package registered. The queued install item is handed to <paramref name="onHandle"/>
    /// as soon as it exists (the caller can cancel it); the poll continues until the Store
    /// finishes. Returns the package after the update and whether its version advanced (a Store
    /// item that completes without registering a new version is NoChange, not a success).
    /// </summary>
    public async Task<UpdateInstallResult> UpdateAsync(
        IProgress<UpdateProgress>? progress,
        CancellationToken ct,
        Action<IStoreInstallHandle>? onHandle = null)
    {
        var installed = await appx.GetInstalledAsync(config.Codex.PackageName, ct).ConfigureAwait(false)
            ?? throw new IOException(
                $"Codex desktop app is not installed as an MSIX package ({config.Codex.PackageName})");
        if (string.IsNullOrWhiteSpace(installed.PackageFamilyName))
        {
            throw new IOException($"the installed package has no PackageFamilyName — cannot address the Store update");
        }

        await CloseCodexAsync(progress, ct).ConfigureAwait(false);

        log.Info($"codex-updater: queuing the Store update for {installed.PackageFamilyName}");
        var handle = await store.StartUpdateAsync(installed.PackageFamilyName, ct).ConfigureAwait(false);
        if (handle is null)
        {
            // the check saw an update, but by now the Store offers none (it may have installed it
            // itself meanwhile): Codex is closed already, so say what happened instead of failing blindly
            log.Warn($"codex-updater: the Store queued no update for {installed.PackageFamilyName}");
            throw new IOException(
                "the Store queued no update for Codex (none is offered any more — it may already be installed); "
                + "Codex was closed for the update, start it again");
        }

        onHandle?.Invoke(handle);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.Codex.Update.UpdateTimeoutSeconds)));
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var status = handle.GetStatus();
                Report(progress, status);
                switch (status.Phase)
                {
                    case StoreInstallPhase.Completed:
                        var after = await appx.GetInstalledAsync(config.Codex.PackageName, ct).ConfigureAwait(false) ?? installed;
                        var changed = !string.Equals(after.Version, installed.Version, StringComparison.Ordinal);
                        if (changed)
                        {
                            log.Info($"codex-updater: installed {installed.Version} -> {after.Version}");
                        }
                        else
                        {
                            log.Warn("codex-updater: version did not change after the Store update — the Store may need another pass");
                        }

                        progress?.Report(changed
                            ? new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {installed.Version} -> {after.Version}")
                            : new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {installed.Version}: the version did not change"));
                        return new UpdateInstallResult(after, changed);

                    case StoreInstallPhase.Error:
                        throw new IOException($"the Store failed to update Codex: {status.Error ?? "unknown error"}");

                    case StoreInstallPhase.Canceled:
                        throw new IOException("the Store canceled the Codex update");

                    case StoreInstallPhase.Downloading or StoreInstallPhase.Installing or StoreInstallPhase.Waiting:
                        await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the caller gave up (Stop, or the window closed): the Store item is queued in the
            // system and would keep downloading and installing without anyone watching
            try
            {
                handle.Cancel();
                log.Info("codex-updater: the Store update was canceled");
            }
            catch (Exception ex)
            {
                log.Warn($"codex-updater: cannot cancel the Store item: {ex.Message}");
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            // the update timed out: the Store keeps the item queued and would install it
            // unattended — cancel it the same way the caller's cancellation does
            try
            {
                handle.Cancel();
                log.Warn("codex-updater: the timed-out Store update was canceled");
            }
            catch (Exception ex)
            {
                log.Warn($"codex-updater: cannot cancel the Store item: {ex.Message}");
            }

            throw new IOException(
                $"the Store update did not finish within {config.Codex.Update.UpdateTimeoutSeconds} s — check the Store app");
        }
    }

    private static void Report(IProgress<UpdateProgress>? progress, StoreInstallStatus status)
    {
        if (progress is null)
        {
            return;
        }

        var phase = status.Phase == StoreInstallPhase.Installing ? UpdatePhase.Install : UpdatePhase.Download;
        progress.Report(new UpdateProgress(phase, (long)status.TotalBytes, (long)status.DoneBytes,
            $"{status.Phase.ToString().ToLowerInvariant()} {status.Percent:0}%"));
    }

    /// <summary>Closing Codex: a polite CloseMainWindow to the windows, a wait, killing the remnants.</summary>
    private async Task CloseCodexAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var running = processes.Find(config.Codex.ProcessName);
        if (running.Count == 0)
        {
            log.Info("codex-updater: Codex is not running");
            return;
        }

        // preparation, not the Install phase: the window moves to its install step on the first
        // Install report and from then on ignores the Store's download progress
        progress?.Report(new UpdateProgress(UpdatePhase.Check, 0, 0, "closing Codex"));
        log.Info($"codex-updater: closing Codex ({running.Count} processes)");
        foreach (var process in running)
        {
            process.CloseMainWindow();
        }

        var waits = Math.Max(1, config.CloseWaitSeconds);
        for (var i = 0; i < waits; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (processes.Find(config.Codex.ProcessName).Count == 0)
            {
                return;
            }
        }

        log.Warn("codex-updater: Codex did not close on its own — killing the process");
        foreach (var process in processes.Find(config.Codex.ProcessName))
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
            {
                // the process exited between the find and the kill — that was the goal anyway
                log.Debug($"codex-updater: a Codex process was already gone when the kill ran: {ex.Message}");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, config.KillSettleSeconds)), ct).ConfigureAwait(false);
    }
}
