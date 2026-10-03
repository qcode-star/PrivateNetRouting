using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Runtime;

namespace PrivateNetRouting.Core.Updater;

public enum UpdatePhase
{
    Check,
    Chain,
    Download,
    Verify,
    Install,
    Done,
}

public sealed record UpdateProgress(UpdatePhase Phase, long TotalBytes, long DoneBytes, string Message);

public sealed record UpdateOptions
{
    public static UpdateOptions Default { get; } = new();

    /// <summary>Взять адрес пакета не из журнала AppX, а явно.</summary>
    public string? ManualUrl { get; init; }

    /// <summary>Игнорировать «версия уже актуальна» (самопроверка скачивания/установки).</summary>
    public bool Force { get; init; }

    /// <summary>Только скачать и проверить пакет, не ставить.</summary>
    public bool DownloadOnly { get; init; }

    /// <summary>Не удалять пакет из кэша после установки.</summary>
    public bool KeepFile { get; init; }

    /// <summary>Не закрывать Claude перед установкой (установка, скорее всего, не пройдёт).</summary>
    public bool KeepRunning { get; init; }
}

/// <summary>Проверка версий: установленная и предлагаемая, без цепочки и без UAC.</summary>
public sealed record UpdateCheckResult(
    InstalledPackage? Installed,
    string? PackageUrl,
    string? OfferedVersion,
    bool UpToDate);

public enum UpdateOutcome
{
    UpToDate,
    Downloaded,
    Installed,
}

public sealed record UpdateRunResult(
    UpdateOutcome Outcome,
    InstalledPackage? Installed,
    InstalledPackage? InstalledAfter,
    string? PackageFile,
    string? PackageUrl);

/// <summary>
/// Ручное обновление Claude Desktop (порт схемы claude-desktop-update): проверка версий
/// ДО подъёма цепочки («нечего ставить» не поднимает ничего и не просит UAC), затем цепочка
/// по необходимости, многопоточная докачка через собственный прокси, сверка Publisher,
/// закрытие Claude и Add-AppxPackage. Пауза перед установкой — решение UI; ядро даёт
/// отдельные фазы и двухфазный запуск (скачали → InstallAsync по подтверждению).
/// Во время докачки держит маркер активности свежим, чтобы автостоп не погасил цепочку
/// под загрузкой при закрытом Claude.
/// </summary>
public sealed class ClaudeUpdater(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IAppxPackageSource appx,
    IUpdateUrlFinder urlFinder,
    IMsixDownloader downloader,
    ActivityMarker activityMarker,
    IClaudeProcessSource claudeProcesses,
    FileLog log,
    Func<string, long>? freeSpaceProvider = null)
{
    /// <summary>Запас свободного места сверх размера пакета (ТЗ прежней схемы).</summary>
    private const long InstallHeadroomBytes = 300L * 1024 * 1024;

    private readonly Func<string, long> _freeSpace = freeSpaceProvider ?? GetFreeSpace;

    public FileLog Log => log;

    public bool IsClaudeRunning() => claudeProcesses.Find(config.Updater.ClaudeProcessName).Count > 0;

    /// <summary>Шаг «Проверка версий»: установленный пакет, адрес из журнала (или ручной), сравнение.</summary>
    public async Task<UpdateCheckResult> CheckAsync(UpdateOptions options, CancellationToken ct)
    {
        var installed = await appx.GetInstalledAsync(config.Updater.PackageName, ct);
        if (installed is null)
        {
            return new UpdateCheckResult(null, null, null, UpToDate: false);
        }

        var url = options.ManualUrl ?? await urlFinder.FindLatestPackageUrlAsync(ct);
        var offered = url is null ? null : PackageUrl.ParseOfferedVersion(url);

        // формат версий отличается: в адресе 2.26454.2, у пакета 2.26454.2.0 — сравнение по префиксу
        var upToDate = offered is not null && installed.Version.StartsWith(offered, StringComparison.Ordinal);
        return new UpdateCheckResult(installed, url, offered, upToDate);
    }

    /// <summary>Шаг «Цепочка»: живой CONNECT-roundtrip; при отказе идемпотентный подъём (возможен UAC).</summary>
    public async Task EnsureChainAsync(IProgress<ChainStep>? chainProgress, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        if (await healthCheck(ct))
        {
            log.Info("updater: chain is alive");
            progress?.Report(new UpdateProgress(UpdatePhase.Chain, 0, 0, "chain is alive"));
            return;
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Chain, 0, 0, "raising the chain"));
        await chain.EnsureRunningAsync(chainProgress, ct);
        if (!await healthCheck(ct))
        {
            throw new ChainException("health", new IOException("chain is up but the CONNECT roundtrip still fails"));
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Chain, 0, 0, "chain is alive"));
    }

    /// <summary>Шаг «Скачивание»: размер, место на диске, докачка фрагментами; возвращает путь к пакету.</summary>
    public async Task<string> DownloadAsync(string url, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var fileName = PackageUrl.ParseFileName(url);
        if (fileName.Length == 0)
        {
            throw new IOException($"the URL does not point to an .msix package: {url}");
        }

        Directory.CreateDirectory(config.Updater.CacheDir);
        var file = Path.Combine(config.Updater.CacheDir, fileName);
        var total = await downloader.GetLengthAsync(url, ct);
        if (total <= 0)
        {
            throw new IOException("the server did not report the package size (Content-Length)");
        }

        activityMarker.Touch();
        var free = _freeSpace(config.Updater.CacheDir);
        if (free >= 0 && free < total + InstallHeadroomBytes)
        {
            throw new IOException($"not enough disk space: need {ByteSize.Format(total + InstallHeadroomBytes)}, available {ByteSize.Format(free)}");
        }

        await downloader.DownloadAsync(url, file, total, new Progress<(long DoneBytes, long TotalBytes)>(value =>
        {
            activityMarker.Touch();
            progress?.Report(new UpdateProgress(UpdatePhase.Download, value.TotalBytes, value.DoneBytes, ""));
        }), ct);
        return file;
    }

    /// <summary>Шаг «Проверка пакета»: манифест на месте, Publisher совпадает с установленным.</summary>
    public async Task<MsixIdentity> VerifyAsync(string file, CancellationToken ct)
    {
        var identity = MsixPackage.TryReadIdentity(file)
            ?? throw new IOException("no AppxManifest.xml in the package — this is not a standalone MSIX; it cannot be installed manually");
        var installed = await appx.GetInstalledAsync(config.Updater.PackageName, ct)
            ?? throw new IOException("Claude Desktop is not installed as an MSIX package");
        if (!string.Equals(identity.Publisher, installed.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "publisher mismatch — the package will not install over the existing one:\n" +
                $"  package:    {identity.Publisher}\n" +
                $"  installed:  {installed.Publisher}");
        }

        log.Info($"updater: package verified Name={identity.Name} Version={identity.Version} Arch={identity.Architecture}");
        return identity;
    }

    /// <summary>
    /// Шаг «Установка» (в т.ч. двухфазный запуск по уже скачанному файлу): повторная сверка
    /// Publisher, закрытие Claude (кроме KeepRunning), Add-AppxPackage, сверка версии,
    /// удаление пакета из кэша (кроме KeepFile). Возвращает пакет после установки.
    /// </summary>
    public async Task<InstalledPackage> InstallAsync(string file, UpdateOptions options, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        await VerifyAsync(file, ct);
        var before = await appx.GetInstalledAsync(config.Updater.PackageName, ct)
            ?? throw new IOException("Claude Desktop is not installed as an MSIX package");

        if (!options.KeepRunning)
        {
            await CloseClaudeAsync(progress, ct);
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Install, 0, 0, "Add-AppxPackage"));
        await appx.AddPackageAsync(file, ct);

        var after = await appx.GetInstalledAsync(config.Updater.PackageName, ct) ?? before;
        if (after.Version == before.Version)
        {
            log.Warn("updater: version did not change after install — check the AppX journal");
        }

        if (!options.KeepFile)
        {
            try
            {
                File.Delete(file);
                log.Info($"updater: package removed from cache: {file}");
            }
            catch (IOException ex)
            {
                log.Warn($"updater: cannot remove the package from cache: {ex.Message}");
            }
        }

        return after;
    }

    /// <summary>Полный цикл без паузы (CLI): проверка → цепочка → скачивание → установка.</summary>
    public async Task<UpdateRunResult> RunAsync(UpdateOptions options, IProgress<UpdateProgress>? progress, IProgress<ChainStep>? chainProgress, CancellationToken ct)
    {
        progress?.Report(new UpdateProgress(UpdatePhase.Check, 0, 0, "checking versions"));
        var check = await CheckAsync(options, ct);
        if (check.Installed is null)
        {
            throw new IOException("Claude Desktop is not installed as an MSIX package — there is nothing to update");
        }

        if (check.UpToDate && !options.Force)
        {
            progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {check.Installed.Version} is up to date (offered {check.OfferedVersion})"));
            return new UpdateRunResult(UpdateOutcome.UpToDate, check.Installed, check.Installed, null, null);
        }

        if (check.PackageUrl is null)
        {
            throw new IOException(
                "package URL not found: the AppX journal has no fresh address. Launch Claude Desktop — its updater " +
                "leaves one about once an hour — or pass the URL explicitly (--url)");
        }

        await EnsureChainAsync(chainProgress, progress, ct);
        var file = await DownloadAsync(check.PackageUrl, progress, ct);
        await VerifyAsync(file, ct);
        if (options.DownloadOnly)
        {
            progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, $"package ready: {file}"));
            return new UpdateRunResult(UpdateOutcome.Downloaded, check.Installed, check.Installed, file, check.PackageUrl);
        }

        var after = await InstallAsync(file, options, progress, ct);
        progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {check.Installed.Version} -> {after.Version}"));
        return new UpdateRunResult(UpdateOutcome.Installed, check.Installed, after, file, check.PackageUrl);
    }

    /// <summary>Закрытие Claude как в прежней схеме: вежливый CloseMainWindow окнам, ожидание, гашение остатков.</summary>
    private async Task CloseClaudeAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var processes = claudeProcesses.Find(config.Updater.ClaudeProcessName);
        if (processes.Count == 0)
        {
            log.Info("updater: Claude is not running");
            return;
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Install, 0, 0, "closing Claude"));
        log.Info("updater: closing Claude");
        foreach (var process in processes)
        {
            process.CloseMainWindow();
        }

        var waits = Math.Max(1, config.Updater.CloseWaitSeconds);
        for (var i = 0; i < waits; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (claudeProcesses.Find(config.Updater.ClaudeProcessName).Count == 0)
            {
                return;
            }
        }

        log.Warn("updater: Claude did not close on its own — killing the process");
        foreach (var process in claudeProcesses.Find(config.Updater.ClaudeProcessName))
        {
            process.Kill();
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, config.Updater.KillSettleSeconds)), ct);
    }

    private static long GetFreeSpace(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
