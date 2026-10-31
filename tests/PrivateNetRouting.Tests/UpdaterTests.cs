using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Runtime;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Update orchestration: version comparison by prefix, "nothing to install" does not touch
/// the chain, manual URL, Publisher check, closing Claude, flags, disk space,
/// parsing URLs from the journal and Get-AppxPackage JSON.
/// </summary>
public class UpdaterTests : IDisposable
{
    private const string MatchingPublisher = "CN=Anthropic LLM";
    private static readonly Uri JournalUrl = new("https://downloads.claude.ai/releases/win32/x64/2.26455.1/Claude-abc123.msix");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pnr-upd-{Guid.NewGuid():N}");
    private readonly FileLog _log;
    private readonly AppConfig _config;
    private readonly FakeChain _chain = new();
    private readonly FakeAppxSource _appx = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakeClaudeSource _claude = new();
    private readonly ActivityMarker _marker;

    public UpdaterTests()
    {
        Directory.CreateDirectory(_root);
        _log = new FileLog(Path.Combine(_root, "updater.log"));
        _config = new AppConfig
        {
            Claude = new ClaudeConfig
            {
                Update = new ClaudeUpdateConfig { CacheDir = Path.Combine(_root, "cache") },
            },
            CloseWaitSeconds = 1,
            KillSettleSeconds = 0,
        };
        _marker = new ActivityMarker(Path.Combine(_root, "activity"), TimeSpan.Zero);
    }

    private FakeUrlFinder _journal = null!;

    private ClaudeUpdater Create(
        bool healthy = true,
        string? journalUrl = null,
        IUpdateUrlFinder? feed = null,
        Func<bool>? health = null,
        Func<string, long>? freeSpace = null,
        bool journalEmpty = false)
    {
        _journal = new FakeUrlFinder(journalEmpty ? null : journalUrl ?? JournalUrl.ToString());
        return new ClaudeUpdater(
            _config,
            _chain,
            _ => Task.FromResult(health is not null ? health() : healthy),
            _appx,
            _journal,
            feed,
            _downloader,
            _marker,
            _claude,
            _log,
            freeSpace);
    }

    // --- Version check ---

    [Fact]
    public async Task CheckComparesVersionByPrefix()
    {
        var updater = Create(journalUrl: "https://downloads.claude.ai/releases/win32/x64/2.26455.1/Claude-x.msix");

        var check = await updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);

        Assert.NotNull(check.Installed);
        Assert.Equal("2.26455.1", check.OfferedVersion);
        Assert.False(check.UpToDate); // 2.26454.2.0 does not start with 2.26455.1
    }

    [Fact]
    public async Task CheckMarksUpToDateByPrefix()
    {
        var updater = Create(journalUrl: "https://downloads.claude.ai/releases/win32/x64/2.26454.2/Claude-x.msix");

        var check = await updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);

        Assert.True(check.UpToDate); // 2.26454.2.0.StartsWith("2.26454.2")
    }

    [Theory]
    [InlineData("2.26454.2", "2.26454.2.0", 0)]
    [InlineData("2.26454.2.0", "2.26454.2", 0)]
    [InlineData("2.26453.0", "2.26454.2.0", -1)]
    [InlineData("2.26455.1.0", "2.26454.2", 1)]
    [InlineData("2.9.0", "2.10.0", -1)] // numeric, not textual
    public void PackageVersionsAreOrderedNumericallyAcrossFormats(string left, string right, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(PackageVersion.Compare(left, right)!.Value));

    [Fact]
    public async Task CheckTreatsAnOlderOfferedVersionAsUpToDate()
    {
        // a stale journal address of an older release is not an update
        var updater = Create(journalUrl: "https://downloads.claude.ai/releases/win32/x64/2.26453.0/Claude-old.msix");

        var check = await updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);

        Assert.True(check.UpToDate);
    }

    [Fact]
    public async Task VerifyRefusesAPackageOlderThanTheInstalledOne()
    {
        _appx.Installed = new InstalledPackage("Claude", "2.26456.0.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");
        var package = await WritePackageAsync(MatchingPublisher); // identity 2.26455.1.0
        var updater = Create();

        var error = await Assert.ThrowsAsync<IOException>(() => updater.VerifyAsync(package, CancellationToken.None));

        Assert.Contains("older than the installed", error.Message);
    }

    [Fact]
    public async Task CheckWithoutInstalledPackageHasNothingToUpdate()
    {
        _appx.Installed = null;
        var updater = Create();

        var check = await updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);

        Assert.Null(check.Installed);
        Assert.False(check.UpToDate);
    }

    [Fact]
    public async Task ManualUrlOverridesJournal()
    {
        var updater = Create(journalUrl: "https://downloads.claude.ai/releases/win32/x64/9.9.9/Claude-old.msix");

        var check = await updater.CheckAsync(
            new UpdateOptions { ManualUrl = "https://downloads.claude.ai/releases/win32/x64/2.26460.0/Claude-new.msix" },
            CancellationToken.None);

        Assert.Equal("2.26460.0", check.OfferedVersion);
        Assert.Contains("2.26460.0", check.PackageUrl);
    }

    [Fact]
    public async Task CheckPrefersFeedWhenProxyAlive()
    {
        var feed = new FakeUrlFinder("https://downloads.claude.ai/releases/win32/x64/2.26456.0/Claude-feed.msix");
        var updater = Create(healthy: true, feed: feed);

        var check = await updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);

        Assert.Equal("2.26456.0", check.OfferedVersion);
        Assert.Equal(1, feed.Calls);
        Assert.Equal(0, _journal.Calls);
    }

    [Fact]
    public async Task CheckSkipsFeedAndFallsBackToJournalWhenProxyDead()
    {
        var feed = new FakeUrlFinder("https://downloads.claude.ai/releases/win32/x64/2.26456.0/Claude-feed.msix");
        var updater = Create(healthy: false, feed: feed);

        var check = await updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);

        Assert.Equal(0, feed.Calls);
        Assert.Equal(1, _journal.Calls);
        Assert.Equal("2.26455.1", check.OfferedVersion);
    }

    [Fact]
    public async Task CheckFallsBackToJournalWhenFeedGivesNothing()
    {
        var updater = Create(healthy: true, feed: new FakeUrlFinder(url: null));

        var check = await updater.CheckAsync(UpdateOptions.Default, CancellationToken.None);

        Assert.Equal(1, _journal.Calls);
        Assert.Equal("2.26455.1", check.OfferedVersion);
    }

    [Fact]
    public async Task ManualUrlConsultsNeitherFeedNorJournal()
    {
        var feed = new FakeUrlFinder("https://downloads.claude.ai/releases/win32/x64/2.26456.0/Claude-feed.msix");
        var updater = Create(healthy: true, feed: feed);

        var check = await updater.CheckAsync(
            new UpdateOptions { ManualUrl = "https://downloads.claude.ai/releases/win32/x64/2.26460.0/Claude-new.msix" },
            CancellationToken.None);

        Assert.Equal(0, feed.Calls);
        Assert.Equal(0, _journal.Calls);
        Assert.Equal("2.26460.0", check.OfferedVersion);
    }

    // --- Full cycle (CLI) ---

    [Fact]
    public async Task UpToDateRunTouchesNeitherChainNorDownloader()
    {
        var updater = Create(
            healthy: false,
            journalUrl: "https://downloads.claude.ai/releases/win32/x64/2.26454.2/Claude-x.msix");

        var result = await updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, _chain.EnsureCalls);
        Assert.Empty(_downloader.Downloads);
    }

    [Fact]
    public async Task MissingUrlFailsAfterChainRaiseWithHintToPassUrl()
    {
        var updater = new ClaudeUpdater(
            _config, _chain, _ => Task.FromResult(true),
            _appx, new FakeUrlFinder(url: null), feedUrlFinder: null, _downloader, _marker, _claude, _log);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None));

        Assert.Contains("package URL not found", error.Message);
    }

    [Fact]
    public async Task RunRaisesChainToAskFeedWhenUrlMissing()
    {
        // both sources are silent on a dead chain (the feed is skipped, the journal is empty);
        // the raise is needed for the URL itself — then the feed answers and the run proceeds
        var feed = new FakeUrlFinder("https://downloads.claude.ai/releases/win32/x64/2.26455.1/Claude-feed.msix");
        var updater = Create(health: () => _chain.EnsureCalls > 0, journalUrl: null, feed: feed);

        var result = await updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Installed, result.Outcome);
        Assert.Equal(1, _chain.EnsureCalls); // the second EnsureChainAsync sees a live chain and is a no-op
        Assert.Single(_downloader.Downloads);
    }

    [Fact]
    public async Task RunDoesNotReinstallTheInstalledVersionOfferedAfterTheChainRaise()
    {
        // the feed is unreachable for the check and the journal is empty: the chain is raised for
        // the URL itself, and the feed then offers the very version that is installed
        var feed = new FakeUrlFinder("https://downloads.claude.ai/releases/win32/x64/2.26454.2/Claude-feed.msix");
        var updater = Create(health: () => _chain.EnsureCalls > 0, journalEmpty: true, feed: feed);

        var result = await updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Empty(_downloader.Downloads);
        Assert.Empty(_appx.AddedFiles);
    }

    [Fact]
    public async Task DeadChainIsRaisedOnceAndCheckedAfter()
    {
        // the chain is considered alive as soon as the orchestrator has raised it (EnsureRunningAsync)
        var updater = Create(health: () => _chain.EnsureCalls > 0);

        var result = await updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Installed, result.Outcome);
        Assert.Equal(1, _chain.EnsureCalls);
    }

    [Fact]
    public async Task ChainFailureStopsBeforeDownload()
    {
        _chain.Failure = new ChainException("daemon", new IOException("UAC declined"));
        var updater = Create(healthy: false);

        await Assert.ThrowsAsync<ChainException>(() =>
            updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None));

        Assert.Empty(_downloader.Downloads);
    }

    [Fact]
    public async Task DownloadOnlyDoesNotInstall()
    {
        var updater = Create();

        var result = await updater.RunAsync(new UpdateOptions { DownloadOnly = true }, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Downloaded, result.Outcome);
        Assert.Empty(_appx.AddedFiles);
        Assert.True(File.Exists(result.PackageFile));
    }

    [Fact]
    public async Task FullRunDownloadsVerifiesInstalls()
    {
        var updater = Create();

        var result = await updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Installed, result.Outcome);
        Assert.Equal("2.26454.2.0", result.Installed?.Version);
        Assert.Equal("2.26455.1.0", result.InstalledAfter?.Version);
        var file = Assert.Single(_appx.AddedFiles);
        Assert.Equal(result.PackageFile, file);
        Assert.StartsWith(_config.Claude.Update.CacheDir, file);
        Assert.False(File.Exists(file)); // the package is removed from the cache after install
    }

    [Fact]
    public async Task AnInstallThatKeepsTheVersionIsNoChange()
    {
        _appx.AfterInstall = _appx.Installed; // Add-AppxPackage returned, the registration did not move
        var updater = Create();
        var progress = new List<UpdateProgress>();

        var result = await updater.RunAsync(
            UpdateOptions.Default,
            new SyncProgress(progress.Add),
            null,
            CancellationToken.None);

        Assert.Equal(UpdateOutcome.NoChange, result.Outcome);
        Assert.Equal("2.26454.2.0", result.InstalledAfter?.Version);
        Assert.Contains(progress, p => p.Phase == UpdatePhase.Done && p.Message.Contains("did not change"));
        Assert.DoesNotContain(progress, p => p.Message == "installed 2.26454.2.0 -> 2.26454.2.0");
    }

    /// <summary>
    /// An address whose path names no version the parser knows leaves the offered version unknown:
    /// the downloaded package is then what tells a reinstall from an update — the installed version
    /// is up to date, Claude is not closed and nothing is installed.
    /// </summary>
    [Fact]
    public async Task APackageOfTheInstalledVersionBehindAnUnknownAddressIsUpToDate()
    {
        _appx.Installed = new InstalledPackage("Claude", "2.26455.1.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");
        var updater = Create(journalUrl: "https://downloads.claude.ai/releases/win32/arm64/2.26455.1/Claude-x.msix");

        var result = await updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Empty(_appx.AddedFiles);
        Assert.Empty(Directory.EnumerateFiles(_config.Claude.Update.CacheDir, "*.msix")); // the useless download is not kept
    }

    [Fact]
    public async Task InstallAsyncOfTheInstalledVersionReinstallsOnlyWithForce()
    {
        _appx.Installed = new InstalledPackage("Claude", "2.26455.1.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");
        var package = await WritePackageAsync(MatchingPublisher); // identity 2.26455.1.0

        var plain = await Create().InstallAsync(package, new UpdateOptions { KeepFile = true }, null, CancellationToken.None);
        Assert.Equal(UpdateOutcome.UpToDate, plain.Outcome);
        Assert.Empty(_appx.AddedFiles);

        await Create().InstallAsync(package, new UpdateOptions { KeepFile = true, Force = true }, null, CancellationToken.None);
        Assert.Single(_appx.AddedFiles);
    }

    /// <summary>
    /// "Changed" is "reached the package's version": a registration that moved somewhere else (or
    /// nowhere) after installing a newer package is a failed update, and --force does not turn it
    /// into an accepted reinstall — only a package of the installed version is a reinstall.
    /// </summary>
    [Fact]
    public async Task AForcedUpdateThatDoesNotReachThePackageIsNotAReinstall()
    {
        _appx.AfterInstall = new InstalledPackage("Claude", "2.26454.3.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");
        var package = await WritePackageAsync(MatchingPublisher); // identity 2.26455.1.0

        var moved = await Create().InstallAsync(package, new UpdateOptions { KeepFile = true, Force = true }, null, CancellationToken.None);

        Assert.False(moved.VersionChanged);
        Assert.Equal(UpdateOutcome.NoChange, moved.Outcome);
        Assert.False(moved.Reinstall);
    }

    [Fact]
    public async Task AForcedReinstallOfTheInstalledVersionIsMarkedAsSuch()
    {
        _appx.Installed = new InstalledPackage("Claude", "2.26455.1.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");
        var package = await WritePackageAsync(MatchingPublisher); // identity 2.26455.1.0

        var reinstall = await Create().InstallAsync(package, new UpdateOptions { KeepFile = true, Force = true }, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.NoChange, reinstall.Outcome);
        Assert.True(reinstall.Reinstall);
    }

    [Fact]
    public async Task InstallAsyncReportsWhetherTheVersionAdvanced()
    {
        var package = await WritePackageAsync(MatchingPublisher);
        _appx.AfterInstall = _appx.Installed; // this pass does not move the version
        var unchanged = await Create().InstallAsync(package, new UpdateOptions { KeepFile = true }, null, CancellationToken.None);
        Assert.False(unchanged.VersionChanged);
        Assert.Equal(UpdateOutcome.NoChange, unchanged.Outcome);

        _appx.AfterInstall = new InstalledPackage("Claude", "2.26456.0.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");
        var advanced = await Create().InstallAsync(package, new UpdateOptions { KeepFile = true }, null, CancellationToken.None);
        Assert.True(advanced.VersionChanged);
        Assert.Equal(UpdateOutcome.Installed, advanced.Outcome);
        Assert.Equal("2.26456.0.0", advanced.Package.Version);
    }

    [Fact]
    public async Task InstallNeverDeletesAFileOutsideTheCache()
    {
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher); // lives under the test root, not in CacheDir

        await updater.InstallAsync(package, UpdateOptions.Default, null, CancellationToken.None);

        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task KeepFileLeavesPackageInCache()
    {
        var updater = Create();

        var result = await updater.RunAsync(new UpdateOptions { KeepFile = true }, null, null, CancellationToken.None);

        Assert.True(File.Exists(result.PackageFile));
    }

    [Fact]
    public async Task ForceProceedsWhenUpToDate()
    {
        var updater = Create(
            healthy: true,
            journalUrl: "https://downloads.claude.ai/releases/win32/x64/2.26454.2/Claude-x.msix");

        var result = await updater.RunAsync(new UpdateOptions { Force = true }, null, null, CancellationToken.None);

        Assert.Equal(UpdateOutcome.Installed, result.Outcome);
        Assert.Single(_downloader.Downloads);
    }

    [Fact]
    public async Task PublisherMismatchStopsBeforeInstall()
    {
        _downloader.Publisher = "CN=Someone Else";
        var updater = Create();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            updater.RunAsync(UpdateOptions.Default, null, null, CancellationToken.None));

        Assert.Contains("publisher mismatch", error.Message);
        Assert.Empty(_appx.AddedFiles);
    }

    // --- Install and closing Claude ---

    [Fact]
    public async Task InstallClosesClaudeWhenItIsRunning()
    {
        var processes = new[] { new FakeClaudeProcess(), new FakeClaudeProcess() };
        _claude.Processes = processes;
        _claude.CloseAfterRequest = true; // the processes disappear after CloseMainWindow
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher);

        await updater.InstallAsync(package, UpdateOptions.Default, null, CancellationToken.None);

        Assert.All(processes, p => Assert.Equal(1, p.Closes));
        Assert.All(processes, p => Assert.Equal(0, p.Kills));
    }

    [Fact]
    public async Task StubbornClaudeIsKilled()
    {
        var processes = new[] { new FakeClaudeProcess() };
        _claude.Processes = processes;
        _claude.CloseAfterRequest = false; // it will not close on its own
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher);

        await updater.InstallAsync(package, UpdateOptions.Default, null, CancellationToken.None);

        Assert.True(processes[0].Closes >= 1);
        Assert.Equal(1, processes[0].Kills);
    }

    [Fact]
    public async Task AProcessGoneBeforeTheKillDoesNotFailTheInstall()
    {
        var process = new FakeClaudeProcess { KillThrows = true };
        _claude.Processes = [process];
        _claude.CloseAfterRequest = false; // the kill path runs
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher);

        await updater.InstallAsync(package, UpdateOptions.Default, null, CancellationToken.None);

        Assert.Equal(1, process.KillAttempts); // the kill ran; its exit race was swallowed
        Assert.Single(_appx.AddedFiles); // the install still went through
    }

    [Fact]
    public async Task APackageOfAnotherApplicationIsRejectedByVerification()
    {
        var package = await WritePackageAsync(MatchingPublisher, name: "SomethingElse");
        var updater = Create();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            updater.VerifyAsync(package, CancellationToken.None));

        Assert.Contains("package name mismatch", error.Message);
        Assert.Empty(_appx.AddedFiles);
    }

    [Fact]
    public async Task APackageOfAnotherArchitectureIsRejectedByVerification()
    {
        var machine = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "x64" : "arm64";
        var package = await WritePackageAsync(MatchingPublisher, architecture: machine);
        var updater = Create();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            updater.VerifyAsync(package, CancellationToken.None));

        Assert.Contains("architecture mismatch", error.Message);
        Assert.Empty(_appx.AddedFiles);
    }

    [Fact]
    public async Task ANeutralArchitecturePackagePassesVerification()
    {
        var package = await WritePackageAsync(MatchingPublisher, architecture: "neutral");
        var updater = Create();

        var identity = await updater.VerifyAsync(package, CancellationToken.None);

        Assert.Equal("neutral", identity.Architecture);
    }

    [Fact]
    public async Task KeepRunningSkipsClosingAndStillInstalls()
    {
        _claude.Processes = [new FakeClaudeProcess()];
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher);

        await updater.InstallAsync(package, new UpdateOptions { KeepRunning = true }, null, CancellationToken.None);

        Assert.All(_claude.Processes, p => Assert.Equal(0, p.Closes));
        Assert.Single(_appx.AddedFiles);
        Assert.Equal([false], _appx.ForcedShutdowns); // the installer must not force it closed either
    }

    [Fact]
    public async Task ADefaultInstallLetsTheInstallerCloseTheApplication()
    {
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher);

        await updater.InstallAsync(package, UpdateOptions.Default, null, CancellationToken.None);

        Assert.Equal([true], _appx.ForcedShutdowns);
    }

    [Fact]
    public void AddPackageScriptCarriesTheForcedShutdownOnlyWhenAsked()
    {
        Assert.Equal(
            "Add-AppxPackage -Path 'C:\\pkg\\it''s.msix' -ForceApplicationShutdown",
            PowerShellAppxPackageSource.AddPackageScript(@"C:\pkg\it's.msix", forceApplicationShutdown: true));
        Assert.Equal(
            "Add-AppxPackage -Path 'C:\\pkg\\it''s.msix'",
            PowerShellAppxPackageSource.AddPackageScript(@"C:\pkg\it's.msix", forceApplicationShutdown: false));
    }

    [Fact]
    public async Task InstallWithoutClaudeRunningDoesNotCloseAnything()
    {
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher);

        await updater.InstallAsync(package, UpdateOptions.Default, null, CancellationToken.None);

        Assert.Equal(1, _claude.FindCalls); // a single "is it running" poll, and nothing after
        Assert.Empty(_claude.Processes);
    }

    [Fact]
    public async Task InstallFailsWhenAddPackageFails()
    {
        _appx.AddFailure = new IOException("deployment blocked (0x80073CF9)");
        var updater = Create();
        var package = await WritePackageAsync(MatchingPublisher);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            updater.InstallAsync(package, UpdateOptions.Default, null, CancellationToken.None));

        Assert.Contains("deployment blocked", error.Message); // the engine text is passed through as is
        Assert.True(File.Exists(package)); // on failure the package is not deleted
    }

    // --- Download: URL, space, marker ---

    [Fact]
    public async Task DownloadRejectsNonMsixUrl()
    {
        var updater = Create();

        await Assert.ThrowsAsync<IOException>(() =>
            updater.DownloadAsync("https://downloads.claude.ai/releases/win32/x64/2.26455.1/Claude.zip", null, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadChecksDiskSpaceBeforeStarting()
    {
        string? asked = null;
        var updater = Create(freeSpace: dir =>
        {
            asked = dir;
            return 1024; // no free space
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            updater.DownloadAsync(JournalUrl.ToString(), null, CancellationToken.None));

        Assert.Contains("not enough disk space", error.Message);
        Assert.Equal(_config.Claude.Update.CacheDir, asked);
        Assert.Empty(_downloader.Downloads);
    }

    [Fact]
    public async Task DownloadKeepsActivityMarkerFresh()
    {
        var updater = Create();

        await updater.DownloadAsync(JournalUrl.ToString(), null, CancellationToken.None);

        Assert.True(File.Exists(_marker.Path)); // the idle stop will not shut the chain down under the download
    }

    [Fact]
    public async Task IsClaudeRunningReflectsProcessSource()
    {
        var updater = Create();
        Assert.False(updater.IsClaudeRunning());
        _claude.Processes = [new FakeClaudeProcess()];
        Assert.True(updater.IsClaudeRunning());
    }

    // --- Package verification ---

    [Fact]
    public async Task VerifyFailsWithoutManifest()
    {
        // archive without AppxManifest.xml, not a single MSIX
        var plain = Path.Combine(_root, "no-manifest.msix");
        using (ZipFile.Open(plain, ZipArchiveMode.Create))
        {
            // empty archive
        }

        var updater = Create();

        var error = await Assert.ThrowsAsync<IOException>(() => updater.VerifyAsync(plain, CancellationToken.None));

        Assert.Contains("no AppxManifest.xml", error.Message);
    }

    // --- URL and JSON parsing ---

    [Theory]
    [InlineData(
        "failed to acquire a placement session for https://downloads.claude.ai/\nreleases/win32/x64/2.26455.1/Claude-abc.msix",
        "https://downloads.claude.ai/releases/win32/x64/2.26455.1/Claude-abc.msix")]
    [InlineData("error 0x80072EE2 for 'https://downloads.claude.ai/pkg/Claude-x.msix?token=1'", "https://downloads.claude.ai/pkg/Claude-x.msix")]
    [InlineData("some other error about https://example.com/a.msix", null)]
    [InlineData(
        "download of https://downloads.claude.ai/a/Claude-1.msix failed, retry https://downloads.claude.ai/b/Claude-2.msix",
        "https://downloads.claude.ai/a/Claude-1.msix")] // two URLs — one whole, not one malformed span
    public void JournalMessageYieldsPackageUrl(string message, string? expected)
    {
        Assert.Equal(expected, PackageUrl.ExtractFromMessage(message, "downloads.claude.ai"));
    }

    [Theory]
    [InlineData("https://downloads.claude.ai/releases/win32/x64/2.26455.1/Claude-abc.msix", "2.26455.1")]
    [InlineData("https://downloads.claude.ai/somewhere/else/Claude-abc.msix", null)]
    public void OfferedVersionComesFromReleasePath(string url, string? expected)
    {
        Assert.Equal(expected, PackageUrl.ParseOfferedVersion(url));
    }

    [Fact]
    public void FeedManifestYieldsCurrentReleaseUrl()
    {
        using var doc = JsonDocument.Parse(
            """
            {"currentRelease":"2.31226.1","releases":[{"version":"2.31226.1","updateTo":{
              "name":"Claude 2.31226.1","version":"2.31226.1","pub_date":"2026-10-09T18:47:13.771693",
              "url":"https://downloads.claude.ai/releases/win32/x64/2.31226.1/Claude-abc.msix",
              "notes":"Production Release - No Notes"}}]}
            """);

        Assert.Equal(
            "https://downloads.claude.ai/releases/win32/x64/2.31226.1/Claude-abc.msix",
            UpdateFeedUrlFinder.ExtractLatestUrl(doc.RootElement, "downloads.claude.ai"));
    }

    [Fact]
    public void FeedManifestPrefersCurrentReleaseAmongMany()
    {
        using var doc = JsonDocument.Parse(
            """
            {"currentRelease":"2.2","releases":[
              {"version":"2.1","updateTo":{"url":"https://downloads.claude.ai/releases/win32/x64/2.1/Claude-one.msix"}},
              {"version":"2.2","updateTo":{"url":"https://downloads.claude.ai/releases/win32/x64/2.2/Claude-two.msix"}}]}
            """);

        Assert.Equal(
            "https://downloads.claude.ai/releases/win32/x64/2.2/Claude-two.msix",
            UpdateFeedUrlFinder.ExtractLatestUrl(doc.RootElement, "downloads.claude.ai"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"releases\":[]}")]
    [InlineData("{\"releases\":[{\"version\":\"2.2\",\"updateTo\":{\"url\":\"https://evil.example.com/x.msix\"}}]}")]
    [InlineData("{\"releases\":[{\"version\":\"2.2\",\"updateTo\":{\"url\":\"https://downloads.claude.ai/x.zip\"}}]}")]
    [InlineData("{\"releases\":[{\"version\":\"2.2\"}]}")]
    public void FeedManifestWithoutUsableUrlYieldsNull(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Null(UpdateFeedUrlFinder.ExtractLatestUrl(doc.RootElement, "downloads.claude.ai"));
    }

    // --- A-03: a package URL must name the package host itself ---

    [Fact]
    public void FeedManifestRejectsAPackageUrlThatOnlyContainsTheHost()
    {
        using var doc = JsonDocument.Parse(
            """
            {"releases":[
              {"version":"2.2","updateTo":{"url":"https://downloads.claude.ai@evil.example/x.msix"}},
              {"version":"2.3","updateTo":{"url":"https://evil.downloads.claude.ai/x.msix"}}]}
            """);

        Assert.Null(UpdateFeedUrlFinder.ExtractLatestUrl(doc.RootElement, "downloads.claude.ai"));
    }

    [Theory]
    [InlineData("https://downloads.claude.ai/releases/win32/x64/2.2/Claude.msix", true)]
    [InlineData("HTTPS://DOWNLOADS.CLAUDE.AI/Claude.msix", true)] // scheme and host are case-insensitive
    [InlineData("https://downloads.claude.ai@evil.example/x.msix", false)] // userinfo trick
    [InlineData("https://evil.downloads.claude.ai/x.msix", false)] // a different host, not a suffix match
    [InlineData("https://evil.example/x.msix", false)]
    [InlineData("http://downloads.claude.ai/x.msix", false)]
    [InlineData("ftp://downloads.claude.ai/x.msix", false)]
    [InlineData("not a url", false)]
    public void APackageUrlMustBeHttpsToThePackageHostWithoutUserinfo(string url, bool expected) =>
        Assert.Equal(expected, PackageUrl.IsTrustedPackageUrl(url, "downloads.claude.ai"));

    [Fact]
    public async Task DownloadRejectsAPackageUrlOffThePackageHost()
    {
        var updater = Create();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            updater.DownloadAsync("https://downloads.claude.ai@evil.example/x.msix", null, CancellationToken.None));

        Assert.Contains("package host", error.Message);
        Assert.Empty(_downloader.Downloads);
    }

    /// <summary>
    /// Redirects are not followed (AllowAutoRedirect=false on the updater client): a 302 to
    /// another host is a failed request, never a download of whatever the Location named.
    /// </summary>
    [Fact]
    public async Task DownloaderDoesNotFollowARedirectOffThePackageHost()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                        var stream = client.GetStream();
                        // the request head is drained before answering: a response written over
                        // an unread request races the client's own write and the connection can
                        // die (RST) before the answer is ever received
                        var buffer = new byte[8192];
                        var received = 0;
                        while (received < buffer.Length)
                        {
                            var read = await stream.ReadAsync(buffer.AsMemory(received));
                            if (read == 0)
                            {
                                break;
                            }

                            received += read;
                            if (Encoding.ASCII.GetString(buffer, 0, received).Contains("\r\n\r\n"))
                            {
                                break;
                            }
                        }

                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 302 Found\r\nLocation: https://evil.example/x.msix\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    }
                }
                catch (Exception)
                {
                    // the listener was stopped: the accept loop ends
                }
            });
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
            var downloader = new FragmentDownloader(
                http,
                new ClaudeUpdateConfig { HeadTimeoutSeconds = 2, ChunkTimeoutSeconds = 2 },
                _log);

            var error = await Assert.ThrowsAsync<IOException>(() =>
                downloader.GetLengthAsync($"http://127.0.0.1:{port}/pkg.msix", CancellationToken.None));

            Assert.Contains("302", error.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FeedDisabledByEmptyUrlYieldsNothingWithoutRequests()
    {
        _config.Claude.Update.UpdateFeedUrl = "";
        using var http = new System.Net.Http.HttpClient();
        var finder = new UpdateFeedUrlFinder(http, _config.Claude.Update, _log);

        Assert.Null(await finder.FindLatestPackageUrlAsync("2.26454.2.0", CancellationToken.None));
    }

    [Theory]
    [InlineData("https://downloads.claude.ai/x/Claude-a.msix?sig=123", "Claude-a.msix")]
    [InlineData("https://downloads.claude.ai/x/Claude-a.zip", "")]
    [InlineData("https://downloads.claude.ai/", "")]
    public void FileNameIgnoresQueryAndRequiresMsix(string url, string expected)
    {
        Assert.Equal(expected, PackageUrl.ParseFileName(url));
    }

    [Theory]
    [InlineData("https://downloads.claude.ai/x/Claude-a.msix", true)]
    [InlineData("https://downloads.claude.ai/x/Claude-a.msix?sig=1", true)]
    [InlineData("http://downloads.claude.ai/x/Claude-a.msix", false)]
    [InlineData("https://downloads.claude.ai/x/Claude-a.zip", false)]
    public void ManualUrlValidityRequiresHttpsAndMsix(string url, bool expected)
    {
        Assert.Equal(expected, PackageUrl.IsValidPackageUrl(url));
    }

    [Fact]
    public void PackageJsonParsesSingleObject()
    {
        var package = PowerShellAppxPackageSource.ParsePackageJson(
            """
            {"Name":"Claude","Version":"2.26454.2.0","Publisher":"CN=Anthropic","InstallLocation":"C:\\Program Files\\WindowsApps\\Claude_1.0.2.0_x64__anthropic"}
            """);

        Assert.NotNull(package);
        Assert.Equal("Claude", package.Name);
        Assert.Equal("2.26454.2.0", package.Version);
        Assert.Equal("CN=Anthropic", package.Publisher);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("[]")]
    public void PackageJsonEmptyOutputMeansNoPackage(string json)
    {
        Assert.Null(PowerShellAppxPackageSource.ParsePackageJson(json));
    }

    /// <summary>A string Sort-Object puts 2.9.0.0 above 2.26454.2.0 — the script must cast to [version].</summary>
    [Fact]
    public async Task InstalledPackageQuerySortsByNumericVersion()
    {
        var scripts = new List<string>();
        var source = new PowerShellAppxPackageSource(
            new ScriptCapturingPowerShell(scripts, (0, """{"Name":"Claude","Version":"2.26454.2.0"}""")),
            new FileLog(Path.Combine(_root, $"appx-{Guid.NewGuid():N}.log")));

        await source.GetInstalledAsync("Claude", CancellationToken.None);

        Assert.Contains(scripts, script => script.Contains("Sort-Object { if ($_.Version) { [version]$_.Version }", StringComparison.Ordinal));
    }

    private sealed class ScriptCapturingPowerShell(List<string> scripts, (int Code, string Output) answer) : IPowerShellExecutor
    {
        public Task<(int Code, string Output)> RunScriptAsync(string script, CancellationToken ct)
        {
            scripts.Add(script);
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public void MsixIdentityIsReadFromManifest()
    {
        var package = WritePackageSync(MatchingPublisher, _root);

        var identity = MsixPackage.TryReadIdentity(package);

        Assert.NotNull(identity);
        Assert.Equal("Claude", identity.Name);
        Assert.Equal("2.26455.1.0", identity.Version);
        Assert.Equal(MatchingPublisher, identity.Publisher);
        Assert.Equal("x64", identity.Architecture);
    }

    [Fact]
    public void MsixWithoutManifestYieldsNullIdentity()
    {
        var path = Path.Combine(_root, "empty.msix");
        using (ZipFile.Open(path, ZipArchiveMode.Create))
        {
        }

        Assert.Null(MsixPackage.TryReadIdentity(path));
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Synchronous collector (Progress&lt;T&gt; posts asynchronously — unusable in asserts).</summary>
    private sealed class SyncProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    private async Task<string> WritePackageAsync(string publisher, string name = "Claude", string architecture = "x64")
    {
        var path = Path.Combine(_root, $"pkg-{Guid.NewGuid():N}.msix");
        await Task.Run(() => WritePackage(path, publisher, name, architecture));
        return path;
    }

    private static string WritePackageSync(string publisher, string root) =>
        WritePackage(Path.Combine(root, $"pnr-pkg-{Guid.NewGuid():N}.msix"), publisher);

    private static string WritePackage(string path, string publisher, string name = "Claude", string architecture = "x64")
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("AppxManifest.xml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{name}" Version="2.26455.1.0" Publisher="{publisher}" ProcessorArchitecture="{architecture}"/>
            </Package>
            """);
        return path;
    }

    private sealed class FakeChain : IChainSupervisor
    {
        public int EnsureCalls { get; private set; }

        public ChainException? Failure { get; set; }

        public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.FromResult(new ChainState(
            DaemonHttpListening: true, DaemonProcessAlive: true, KeeperRunning: true,
            BackendState: "Running", ProxyRunning: true, ProxyHealthy: true));

        public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct)
        {
            EnsureCalls++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAppxSource : IAppxPackageSource
    {
        public InstalledPackage? Installed { get; set; } = new("Claude", "2.26454.2.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");

        public InstalledPackage? AfterInstall { get; set; } = new("Claude", "2.26455.1.0", MatchingPublisher, @"C:\WindowsApps\Claude", "Claude_abc123");

        public List<string> AddedFiles { get; } = [];

        /// <summary>The forceApplicationShutdown flag of every installation, in order.</summary>
        public List<bool> ForcedShutdowns { get; } = [];

        public Exception? AddFailure { get; set; }

        public Task<InstalledPackage?> GetInstalledAsync(string packageName, CancellationToken ct) =>
            Task.FromResult(Installed);

        public Task AddPackageAsync(string packageFile, bool forceApplicationShutdown, CancellationToken ct)
        {
            AddedFiles.Add(packageFile);
            ForcedShutdowns.Add(forceApplicationShutdown);
            if (AddFailure is not null)
            {
                return Task.FromException(AddFailure);
            }

            Installed = AfterInstall;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUrlFinder(string? url) : IUpdateUrlFinder
    {
        public int Calls { get; private set; }

        public Task<string?> FindLatestPackageUrlAsync(string? installedVersion, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(url);
        }
    }

    private sealed class FakeDownloader : IMsixDownloader
    {
        public string Publisher { get; set; } = MatchingPublisher;

        public List<string> Downloads { get; } = [];

        public Task<long> GetLengthAsync(string url, CancellationToken ct) => Task.FromResult(1_000L);

        public async Task DownloadAsync(string url, string targetFile, long expectedLength, IProgress<(long DoneBytes, long TotalBytes)>? progress, CancellationToken ct)
        {
            Downloads.Add(url);
            progress?.Report((expectedLength / 2, expectedLength));
            await Task.Run(() => WritePackage(targetFile, Publisher));
            progress?.Report((expectedLength, expectedLength));
        }
    }

    private sealed class FakeClaudeProcess : IClaudeProcessHandle
    {
        public int Closes { get; private set; }

        public int Kills { get; private set; }

        public int KillAttempts { get; private set; }

        /// <summary>The process exits between the find and the kill (the kill throws).</summary>
        public bool KillThrows { get; set; }

        public bool CloseMainWindow()
        {
            Closes++;
            return true;
        }

        public void Kill()
        {
            KillAttempts++;
            if (KillThrows)
            {
                throw new InvalidOperationException("process has already exited");
            }

            Kills++;
        }
    }

    private sealed class FakeClaudeSource : IClaudeProcessSource
    {
        public IReadOnlyList<FakeClaudeProcess> Processes { get; set; } = [];

        /// <summary>Processes disappear as soon as they are asked to close (polite exit).</summary>
        public bool CloseAfterRequest { get; set; }

        public int FindCalls { get; private set; }

        public IReadOnlyList<IClaudeProcessHandle> Find(string processName)
        {
            FindCalls++;
            if (CloseAfterRequest && Processes.All(p => p.Closes > 0))
            {
                return [];
            }

            return Processes.Cast<IClaudeProcessHandle>().ToList();
        }
    }
}
