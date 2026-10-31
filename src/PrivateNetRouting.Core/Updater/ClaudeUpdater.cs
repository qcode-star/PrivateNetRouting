using System.Runtime.InteropServices;
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

    /// <summary>Take the package URL explicitly instead of from the AppX journal.</summary>
    public string? ManualUrl { get; init; }

    /// <summary>Ignore "version is already up to date" (self-check of download/install).</summary>
    public bool Force { get; init; }

    /// <summary>Only download and verify the package, do not install.</summary>
    public bool DownloadOnly { get; init; }

    /// <summary>Do not delete the package from the cache after install.</summary>
    public bool KeepFile { get; init; }

    /// <summary>Do not close Claude before install (the install will most likely fail).</summary>
    public bool KeepRunning { get; init; }
}

/// <summary>Version check: installed and offered, without the chain and without UAC.</summary>
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

    /// <summary>The install/Store pass completed, but the observed version did not advance.</summary>
    NoChange,
}

public sealed record UpdateRunResult(
    UpdateOutcome Outcome,
    InstalledPackage? Installed,
    InstalledPackage? InstalledAfter,
    string? PackageFile,
    string? PackageUrl,
    bool Reinstall = false);

/// <summary>
/// What an install pass left behind: the package seen afterwards and whether its version really
/// advanced. The outcome is derived from the observation, not from "the command returned", so a
/// silent AppX/Store failure is NoChange rather than a success with a run of identical versions.
/// VersionChanged means the registration reached the package's version — not merely "moved".
/// AlreadyInstalled — the package is the installed version and no reinstall was asked for
/// (--force): nothing was closed or installed, the outcome is UpToDate. Reinstall — the forced
/// pass of a package of the installed version: its NoChange is the expected end, while a NoChange
/// of a newer package stays a failed update even under --force.
/// </summary>
public sealed record UpdateInstallResult(InstalledPackage Package, bool VersionChanged, bool AlreadyInstalled = false, bool Reinstall = false)
{
    public UpdateOutcome Outcome =>
        AlreadyInstalled ? UpdateOutcome.UpToDate
        : VersionChanged ? UpdateOutcome.Installed
        : UpdateOutcome.NoChange;
}

/// <summary>
/// Manual update of Claude Desktop: version check
/// BEFORE bringing up the chain ("nothing to install" brings up nothing and asks for no UAC), then the chain
/// if needed, chunked download via its own proxy, Publisher verification,
/// closing Claude and Add-AppxPackage. The pause before install is a UI decision; the core exposes
/// separate phases and a two-phase run (downloaded -> InstallAsync on confirmation).
/// During the download it keeps the activity marker fresh so the autostop does not bring the chain down
/// under the download while Claude is closed.
/// </summary>
public sealed class ClaudeUpdater(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IAppxPackageSource appx,
    IUpdateUrlFinder urlFinder,
    IUpdateUrlFinder? feedUrlFinder,
    IMsixDownloader downloader,
    ActivityMarker activityMarker,
    IClaudeProcessSource claudeProcesses,
    FileLog log,
    Func<string, long>? freeSpaceProvider = null)
{
    /// <summary>Headroom of free space above the package size.</summary>
    private const long InstallHeadroomBytes = 300L * 1024 * 1024;

    private readonly Func<string, long> _freeSpace = freeSpaceProvider ?? GetFreeSpace;

    public FileLog Log => log;

    public bool IsClaudeRunning() => claudeProcesses.Find(config.Claude.ProcessName).Count > 0;

    /// <summary>Step "Version check": installed package, URL from the feed/journal (or manual), comparison.</summary>
    public async Task<UpdateCheckResult> CheckAsync(UpdateOptions options, CancellationToken ct)
    {
        var installed = await appx.GetInstalledAsync(config.Claude.Update.PackageName, ct);
        if (installed is null)
        {
            return new UpdateCheckResult(null, null, null, UpToDate: false);
        }

        var url = await FindPackageUrlAsync(options, installed.Version, ct);
        var offered = url is null ? null : PackageUrl.ParseOfferedVersion(url);

        return new UpdateCheckResult(installed, url, offered, IsUpToDate(installed.Version, offered));
    }

    /// <summary>
    /// Up to date means the offered version is not newer than the installed one: a stale address
    /// of an older release (an old journal entry) is not an update. Versions that do not parse
    /// fall back to the prefix comparison.
    /// </summary>
    private static bool IsUpToDate(string installedVersion, string? offered)
    {
        if (offered is null)
        {
            return false;
        }

        return PackageVersion.Compare(installedVersion, offered) is { } order
            ? order >= 0
            : installedVersion.StartsWith(offered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Package address, in order: manual; the update feed (only when the proxy is already
    /// alive — the check never brings the chain up and asks for no UAC); the AppX journal.
    /// </summary>
    private async Task<string?> FindPackageUrlAsync(UpdateOptions options, string installedVersion, CancellationToken ct)
    {
        if (options.ManualUrl is not null)
        {
            return options.ManualUrl;
        }

        string? url = null;
        if (feedUrlFinder is not null && await healthCheck(ct))
        {
            url = await feedUrlFinder.FindLatestPackageUrlAsync(installedVersion, ct);
        }

        return url ?? await urlFinder.FindLatestPackageUrlAsync(installedVersion, ct);
    }

    /// <summary>Step "Chain": live CONNECT roundtrip; on failure an idempotent bring-up (UAC possible).</summary>
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

    /// <summary>Step "Download": size, disk space, chunked download; returns the path to the package.</summary>
    public async Task<string> DownloadAsync(string url, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var fileName = PackageUrl.ParseFileName(url);
        if (fileName.Length == 0)
        {
            throw new IOException($"the URL does not point to an .msix package: {url}");
        }

        // the last gate before the download: the feed, the journal and the manual entry all pass
        // through here, and none of them may point the download off the package host (a userinfo
        // trick like https://package.host@evil.example/x.msix would pass a substring check)
        if (!PackageUrl.IsTrustedPackageUrl(url, config.Claude.Update.PackageHost))
        {
            throw new IOException($"the package URL does not point at the package host '{config.Claude.Update.PackageHost}': {url}");
        }

        Directory.CreateDirectory(config.Claude.Update.CacheDir);
        var file = Path.Combine(config.Claude.Update.CacheDir, fileName);
        var total = await downloader.GetLengthAsync(url, ct);
        if (total <= 0)
        {
            throw new IOException("the server did not report the package size (Content-Length)");
        }

        activityMarker.Touch();
        var free = _freeSpace(config.Claude.Update.CacheDir);
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

    /// <summary>Step "Package verification": the manifest is present, the identity matches the installed package and this machine.</summary>
    public async Task<MsixIdentity> VerifyAsync(string file, CancellationToken ct)
    {
        var identity = MsixPackage.TryReadIdentity(file)
            ?? throw new IOException("no AppxManifest.xml in the package — this is not a standalone MSIX; it cannot be installed manually");
        var installed = await appx.GetInstalledAsync(config.Claude.Update.PackageName, ct)
            ?? throw new IOException("Claude Desktop is not installed as an MSIX package");
        if (!string.Equals(identity.Name, installed.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "package name mismatch — the package is not the installed application:\n" +
                $"  package:    {identity.Name}\n" +
                $"  installed:  {installed.Name}");
        }

        if (!string.Equals(identity.Publisher, installed.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "publisher mismatch — the package will not install over the existing one:\n" +
                $"  package:    {identity.Publisher}\n" +
                $"  installed:  {installed.Publisher}");
        }

        var machineArch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        if (!string.Equals(identity.Architecture, machineArch, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(identity.Architecture, "neutral", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"architecture mismatch — the package is built for another machine:\n" +
                $"  package:    {identity.Architecture}\n" +
                $"  machine:    {machineArch}");
        }

        // an older package cannot be installed over a newer one (0x80073D06) — find out before
        // Claude is closed, not after
        if (PackageVersion.Compare(identity.Version, installed.Version) is < 0)
        {
            throw new IOException(
                $"the package is older than the installed one ({identity.Version} < {installed.Version}) — it cannot be installed over it");
        }

        log.Info($"updater: package verified Name={identity.Name} Version={identity.Version} Arch={identity.Architecture}");
        return identity;
    }

    /// <summary>
    /// Step "Install" (including the two-phase run on an already downloaded file): repeated Publisher
    /// verification, closing Claude (unless KeepRunning), Add-AppxPackage, version verification,
    /// deleting the package from the cache (unless KeepFile). Returns the package after install and
    /// whether its version advanced. A package of the installed version is a reinstall only with
    /// Force; otherwise nothing is closed or installed and the result is AlreadyInstalled.
    /// </summary>
    public async Task<UpdateInstallResult> InstallAsync(string file, UpdateOptions options, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var identity = await VerifyAsync(file, ct);
        var before = await appx.GetInstalledAsync(config.Claude.Update.PackageName, ct)
            ?? throw new IOException("Claude Desktop is not installed as an MSIX package");

        if (IsInstalledVersion(identity, before.Version) && !options.Force)
        {
            log.Info($"updater: the package is the installed version {before.Version} — nothing to install");
            if (!options.KeepFile)
            {
                DiscardDownloaded(file);
            }

            return new UpdateInstallResult(before, false, AlreadyInstalled: true);
        }

        if (!options.KeepRunning)
        {
            await CloseClaudeAsync(progress, ct);
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Install, 0, 0, "Add-AppxPackage"));
        await appx.AddPackageAsync(file, ct);

        // only a forced pass gets here with the installed version: a reinstall, not an update
        var reinstall = IsInstalledVersion(identity, before.Version);
        var after = await appx.GetInstalledAsync(config.Claude.Update.PackageName, ct) ?? before;
        var changed = !reinstall && ReachedPackageVersion(after.Version, identity.Version);
        if (!changed && !reinstall)
        {
            log.Warn($"updater: the installed version is {after.Version} after the install, not the package's {identity.Version} — check the AppX journal");
        }

        if (!options.KeepFile)
        {
            DiscardDownloaded(file);
        }

        return new UpdateInstallResult(after, changed, Reinstall: reinstall);
    }

    /// <summary>The registration reached the package (or went past it — another update landed meanwhile).</summary>
    private static bool ReachedPackageVersion(string after, string package) =>
        PackageVersion.Compare(after, package) is { } order
            ? order >= 0
            : string.Equals(after, package, StringComparison.Ordinal);

    /// <summary>
    /// The package carries the installed version: an address whose path names no version (a manual
    /// --url, a changed feed format) leaves the offered version unknown until the download, so the
    /// verified manifest is what tells a reinstall from an update.
    /// </summary>
    public static bool IsInstalledVersion(MsixIdentity identity, string installedVersion) =>
        PackageVersion.Compare(identity.Version, installedVersion) is { } order
            ? order == 0
            : string.Equals(identity.Version, installedVersion, StringComparison.Ordinal);

    /// <summary>Deletes a package the updater downloaded itself; a file the user pointed at is never touched.</summary>
    public void DiscardDownloaded(string file)
    {
        if (!IsInCache(file))
        {
            return;
        }

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

    /// <summary>Only a package the updater downloaded itself is ours to delete — never a file the user pointed at.</summary>
    private bool IsInCache(string file)
    {
        var cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Claude.Update.CacheDir));
        return Path.GetFullPath(file).StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full cycle without a pause (CLI): check -> chain -> download -> install.</summary>
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

        var packageUrl = check.PackageUrl;
        if (packageUrl is null)
        {
            // neither source has an address without the chain (the feed is unreachable, the
            // journal is empty): the run raises the chain anyway — then the feed answers
            await EnsureChainAsync(chainProgress, progress, ct);
            packageUrl = await FindPackageUrlAsync(options, check.Installed.Version, ct)
                ?? throw new IOException(
                    "package URL not found: the update feed gave nothing and the AppX journal has no fresh address. " +
                    "Pass the URL explicitly (--url)");

            // the feed was out of reach for the check: now that it has answered, the offered
            // version may well be the installed one — do not download and reinstall it
            var offered = PackageUrl.ParseOfferedVersion(packageUrl);
            if (IsUpToDate(check.Installed.Version, offered) && !options.Force)
            {
                progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {check.Installed.Version} is up to date (offered {offered})"));
                return new UpdateRunResult(UpdateOutcome.UpToDate, check.Installed, check.Installed, null, null);
            }
        }

        await EnsureChainAsync(chainProgress, progress, ct);
        var file = await DownloadAsync(packageUrl, progress, ct);
        var identity = await VerifyAsync(file, ct);
        if (IsInstalledVersion(identity, check.Installed.Version) && !options.Force)
        {
            // the address named no version (or one the parser does not know): the package itself
            // says it is the installed release — Claude is not closed for a reinstall nobody asked for
            if (!options.KeepFile)
            {
                DiscardDownloaded(file);
            }

            progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {check.Installed.Version} is up to date (the package is the same version)"));
            return new UpdateRunResult(UpdateOutcome.UpToDate, check.Installed, check.Installed, null, packageUrl);
        }

        if (options.DownloadOnly)
        {
            progress?.Report(new UpdateProgress(UpdatePhase.Done, 0, 0, $"package ready: {file}"));
            return new UpdateRunResult(UpdateOutcome.Downloaded, check.Installed, check.Installed, file, packageUrl);
        }

        var install = await InstallAsync(file, options, progress, ct);
        progress?.Report(install.VersionChanged
            ? new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {check.Installed.Version} -> {install.Package.Version}")
            : new UpdateProgress(UpdatePhase.Done, 0, 0, $"installed {check.Installed.Version}: the version did not change"));
        return new UpdateRunResult(install.Outcome, check.Installed, install.Package, file, packageUrl, install.Reinstall);
    }

    /// <summary>Closing Claude: a polite CloseMainWindow to the windows, a wait, killing the remnants.</summary>
    private async Task CloseClaudeAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var processes = claudeProcesses.Find(config.Claude.ProcessName);
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

        var waits = Math.Max(1, config.CloseWaitSeconds);
        for (var i = 0; i < waits; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (claudeProcesses.Find(config.Claude.ProcessName).Count == 0)
            {
                return;
            }
        }

        log.Warn("updater: Claude did not close on its own — killing the process");
        foreach (var process in claudeProcesses.Find(config.Claude.ProcessName))
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
            {
                // the process exited between the find and the kill — that was the goal anyway;
                // the app is already closed, the install must not fail on it
                log.Debug($"updater: a Claude process was already gone when the kill ran: {ex.Message}");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, config.KillSettleSeconds)), ct);
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
