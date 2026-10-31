using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Updater;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Tests;

/// <summary>Codex desktop app support: the launch script, the launch order, the Store update check/update.</summary>
public class CodexTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-codex-{Guid.NewGuid():N}.log");
    private readonly FileLog _log;
    private readonly FakeChain _chain = new();
    private readonly FakeProcessFinder _processes = new();

    public CodexTests()
    {
        _log = new FileLog(_logPath);
    }

    private CodexLauncher CreateLauncher(
        AppConfig? config = null,
        bool healthy = true,
        FakePowerShell? powershell = null,
        IRunningAppCloser? closer = null)
    {
        config ??= DefaultConfig();
        powershell ??= new FakePowerShell();
        return new CodexLauncher(
            config,
            _chain,
            _ => Task.FromResult(healthy),
            _processes,
            _log,
            powershell,
            appCloser: closer);
    }

    /// <summary>A process of the installed Codex package (the scan is filtered by the package name).</summary>
    private static FoundProcess CodexProcess(int id, string extraArgs = "") =>
        new(id, $@"C:\Program Files\WindowsApps\OpenAI.Codex_26.0.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe{extraArgs}");

    // FileLog keeps the file open for writing, so read with ReadWrite sharing
    private string ReadLog()
    {
        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private readonly string _envPath = Path.Combine(Path.GetTempPath(), $"pnr-codex-env-{Guid.NewGuid():N}");

    private AppConfig DefaultConfig() => new()
    {
        ForwardProxy = new ForwardProxyConfig { Listen = "127.0.0.1:18082" },
        BypassDomains = ["corp.example.com"],
        Codex = new CodexConfig
        {
            PackageName = "OpenAI.Codex",
            AppId = "App",
            AppExecutable = @"app\ChatGPT.exe",
            ProcessName = "ChatGPT",
            EnvPath = _envPath,
        },
    };

    // --- launcher ---

    [Fact]
    public void LaunchScriptCarriesEnvFlagsAndPackageContext()
    {
        var launcher = CreateLauncher();

        var script = launcher.BuildLaunchScript();

        // the env is what reaches the embedded CLI core (config.toml has no proxy key)
        Assert.Contains("$env:HTTP_PROXY = 'http://127.0.0.1:18082'", script);
        Assert.Contains("$env:HTTPS_PROXY = 'http://127.0.0.1:18082'", script);
        Assert.Contains("$env:ALL_PROXY = 'http://127.0.0.1:18082'", script);
        Assert.Contains("$env:NO_PROXY = '", script);
        // the Chromium flags ride -Args as one string
        Assert.Contains("--proxy-server=http://127.0.0.1:18082", script);
        Assert.Contains("--proxy-bypass-list=", script);
        // the package-context launch targets the manifest entry point (the stub loses arguments)
        Assert.Contains("Get-AppxPackage -Name 'OpenAI.Codex'", script);
        Assert.Contains("-AppId 'App'", script);
        Assert.Contains("Join-Path $pkg.InstallLocation 'app\\ChatGPT.exe'", script);
        Assert.Contains("Invoke-CommandInDesktopPackage", script);
    }

    [Fact]
    public void LaunchScriptEscapesSingleQuotes()
    {
        var config = DefaultConfig();
        config.BypassDomains = ["o'brien.example"];

        var script = new CodexLauncher(
            config, _chain, _ => Task.FromResult(true), _processes, _log, new FakePowerShell())
            .BuildLaunchScript();

        Assert.Contains("o''brien.example", script);
    }

    [Fact]
    public async Task HealthyChainLaunchesWithoutRaising()
    {
        var powershell = new FakePowerShell();
        var launcher = CreateLauncher(healthy: true, powershell: powershell);

        var result = await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.Equal(CodexLaunchOutcome.Launched, result.Outcome);
        Assert.False(result.ChainRaised);
        Assert.Equal(0, _chain.EnsureCalls);
        Assert.Equal(1, powershell.Calls);
        Assert.Contains("PNR=ok", powershell.LastScript);
    }

    [Fact]
    public async Task LaunchRefreshesTheEnvFileTheCoreLoads()
    {
        var config = DefaultConfig();
        // what a WSL codex shell leaves behind: the NAT gateway address, unreachable from Windows
        // once WSL is down
        File.WriteAllText(config.Codex.EnvPath, "HTTP_PROXY=http://192.0.2.1:18082\nMY_VAR=keep\n");
        var powershell = new FakePowerShell();
        var launcher = CreateLauncher(config, healthy: true, powershell: powershell);

        await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        var text = File.ReadAllText(config.Codex.EnvPath);
        Assert.Contains("HTTP_PROXY=http://127.0.0.1:18082\n", text);
        Assert.Contains("MY_VAR=keep\n", text); // the foreign line survives
        Assert.DoesNotContain("192.0.2.1", text);
    }

    [Fact]
    public async Task DeadChainIsRaisedBeforeLaunch()
    {
        var powershell = new FakePowerShell();
        var launcher = CreateLauncher(healthy: false, powershell: powershell);

        var result = await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.True(result.ChainRaised);
        Assert.Equal(1, _chain.EnsureCalls);
        Assert.Equal(1, powershell.Calls);
    }

    [Fact]
    public async Task CheckOnlyRaisesChainButDoesNotStartCodex()
    {
        var powershell = new FakePowerShell();
        var launcher = CreateLauncher(healthy: false, powershell: powershell);

        var result = await launcher.LaunchAsync(new LauncherOptions(CheckOnly: true), null, CancellationToken.None);

        Assert.Equal(CodexLaunchOutcome.CheckOnly, result.Outcome);
        Assert.Equal(1, _chain.EnsureCalls);
        Assert.Equal(0, powershell.Calls);
    }

    // --- an already running copy: reported, and closed only on the owner's answer ---

    [Fact]
    public void RunningCodexWithOurProxyNeedsNoRestart()
    {
        _processes.Processes = [CodexProcess(42, " --proxy-server=http://127.0.0.1:18082")];
        var launcher = CreateLauncher();

        var running = launcher.InspectRunning();

        Assert.Equal(RunningAppState.Proxied, running.State);
        Assert.Equal(1, running.ProcessCount);
    }

    [Fact]
    public void RunningCodexWithAnotherProxyIsAlsoARestartCandidate()
    {
        _processes.Processes = [CodexProcess(42, " --proxy-server=http://127.0.0.1:9999")];
        var launcher = CreateLauncher();

        var running = launcher.InspectRunning();

        Assert.Equal(RunningAppState.WithoutProxy, running.State);
        Assert.Equal("http://127.0.0.1:9999", running.ForeignProxyUrl);
    }

    [Fact]
    public void ForeignChatGptProcessesAreNotMistakenForCodex()
    {
        // another application may share the executable name: only the package path counts
        _processes.Processes = [new FoundProcess(7, @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.0\app\ChatGPT.exe")];
        var launcher = CreateLauncher();

        Assert.Equal(RunningAppState.NotRunning, launcher.InspectRunning().State);
    }

    [Fact]
    public async Task RunningCopyWithoutProxyIsWarnedAboutWhenTheRestartIsNotAsked()
    {
        _processes.Processes = [CodexProcess(42)];
        var launcher = CreateLauncher();

        var result = await launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None);

        Assert.False(result.RestartedExisting);
        Assert.Contains("without --proxy-server", ReadLog());
    }

    [Fact]
    public async Task RestartClosesTheRunningCopyBeforeStarting()
    {
        _processes.Processes = [CodexProcess(42), CodexProcess(43, " --type=renderer")];
        var closer = new FakeAppCloser { Alive = { 42, 43 } };
        var launcher = CreateLauncher(closer: closer);

        var result = await launcher.LaunchAsync(new LauncherOptions(CheckOnly: false, RestartRunning: true), null, CancellationToken.None);

        Assert.True(result.RestartedExisting);
        Assert.Equal(2, closer.GracefulCalls.Count);
        Assert.Empty(closer.KillCalls); // the window close of the main process was enough
        Assert.Empty(closer.Alive);
        Assert.Contains("closed to restart through the proxy", ReadLog());
    }

    [Fact]
    public async Task RestartKillsWhatSurvivedTheWindowClose()
    {
        _processes.Processes = [CodexProcess(42)];
        var closer = new FakeAppCloser { Alive = { 42 }, NoWindow = { 42 } };
        var launcher = CreateLauncher(closer: closer);

        var result = await launcher.LaunchAsync(new LauncherOptions(CheckOnly: false, RestartRunning: true), null, CancellationToken.None);

        Assert.True(result.RestartedExisting);
        Assert.Equal(new[] { 42 }, closer.KillCalls);
        Assert.Empty(closer.Alive);
    }

    [Fact]
    public async Task CheckOnlyNeverClosesARunningCopy()
    {
        _processes.Processes = [CodexProcess(42)];
        var closer = new FakeAppCloser { Alive = { 42 } };
        var launcher = CreateLauncher(closer: closer);

        await launcher.LaunchAsync(new LauncherOptions(CheckOnly: true, RestartRunning: true), null, CancellationToken.None);

        Assert.Empty(closer.GracefulCalls);
        Assert.Empty(closer.KillCalls);
    }

    [Fact]
    public async Task LaunchScriptFailureStopsTheLaunch()
    {
        var powershell = new FakePowerShell { Code = 1, Output = "Codex desktop app is not installed" };
        var launcher = CreateLauncher(powershell: powershell);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            launcher.LaunchAsync(LauncherOptions.Default, null, CancellationToken.None));

        Assert.Contains("not installed", ex.Message);
    }

    // --- update check ---

    [Fact]
    public async Task CheckReportsStoreUpdateWithManifestVersion()
    {
        var (updater, store, appx, _, manifest) = CreateUpdater();
        store.HasUpdate = true;
        manifest.Response = """
            {"schemaVersion": 1, "buildVersion": "26.1101.0.0", "storeProductId": "9PLM9XGG6VKS", "packageIdentity": "OpenAI.Codex"}
            """;
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");

        var check = await updater.CheckAsync(CancellationToken.None);

        Assert.True(check.UpdateAvailable);
        Assert.False(check.StoreUnavailable);
        Assert.Equal("26.1101.0.0", check.OfferedVersion);
        Assert.Equal("9PLM9XGG6VKS", store.LastProductId);
    }

    [Fact]
    public async Task CheckWithoutManifestStillReportsTheUpdate()
    {
        var (updater, store, appx, _, manifest) = CreateUpdater();
        store.HasUpdate = true;
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");
        manifest.Response = "{}"; // no buildVersion — the update still exists

        var check = await updater.CheckAsync(CancellationToken.None);

        Assert.True(check.UpdateAvailable);
        Assert.Null(check.OfferedVersion);
    }

    [Fact]
    public async Task StoreFailureIsNotAnError()
    {
        var (updater, store, appx, _, _) = CreateUpdater();
        store.Error = new IOException("Store is offline");
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");

        var check = await updater.CheckAsync(CancellationToken.None);

        Assert.True(check.StoreUnavailable);
        Assert.False(check.UpdateAvailable);
    }

    [Fact]
    public async Task CheckWithoutPackageFails()
    {
        var (updater, _, appx, _, _) = CreateUpdater();
        appx.Installed = null;

        await Assert.ThrowsAsync<IOException>(() => updater.CheckAsync(CancellationToken.None));
    }

    // --- update run ---

    [Fact]
    public async Task UpdateClosesAppQueuesStoreAndFollowsToCompletion()
    {
        var (updater, store, appx, processes, _) = CreateUpdater();
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");
        appx.AfterInstall = Package("26.1101.0.0", "OpenAI.Codex_2p2nqsd0c76g0");
        processes.FindResult = [new FakeProcessHandle()];
        store.Handle.StatusSequence = [
            new StoreInstallStatus(StoreInstallPhase.Downloading, 100, 30, 30, null),
            new StoreInstallStatus(StoreInstallPhase.Installing, 100, 100, 100, null),
            new StoreInstallStatus(StoreInstallPhase.Completed, 100, 100, 100, null),
        ];

        var progress = new List<UpdateProgress>();
        var update = await updater.UpdateAsync(new Progress<UpdateProgress>(progress.Add), CancellationToken.None);

        Assert.True(update.VersionChanged);
        Assert.Equal("26.1101.0.0", update.Package.Version);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", store.LastFamilyName);
        Assert.True(processes.FindResult[0].CloseMainWindowCalled);
        Assert.Contains(progress, p => p.Phase == UpdatePhase.Download);
        Assert.Contains(progress, p => p.Phase == UpdatePhase.Install);
    }

    [Fact]
    public async Task AStoreUpdateThatKeepsTheVersionIsNoChange()
    {
        var (updater, store, appx, _, _) = CreateUpdater();
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");
        appx.AfterInstall = appx.Installed; // the Store item completed, the registration did not move
        store.Handle.StatusSequence = [
            new StoreInstallStatus(StoreInstallPhase.Downloading, 100, 100, 100, null),
            new StoreInstallStatus(StoreInstallPhase.Completed, 100, 100, 100, null),
        ];
        var progress = new List<UpdateProgress>();

        var update = await updater.UpdateAsync(new Progress<UpdateProgress>(progress.Add), CancellationToken.None);

        Assert.False(update.VersionChanged);
        Assert.Equal(UpdateOutcome.NoChange, update.Outcome);
        Assert.Equal("26.1007.2314.0", update.Package.Version);
        Assert.Contains(progress, p => p.Phase == UpdatePhase.Done && p.Message.Contains("did not change"));
        Assert.DoesNotContain(progress, p => p.Message == "installed 26.1007.2314.0 -> 26.1007.2314.0");
    }

    [Fact]
    public async Task ClosingCodexIsNotReportedAsTheInstallPhase()
    {
        var (updater, store, appx, processes, _) = CreateUpdater();
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");
        appx.AfterInstall = Package("26.1101.0.0", "OpenAI.Codex_2p2nqsd0c76g0");
        processes.FindResult = [new FakeProcessHandle()];
        store.Handle.StatusSequence = [
            new StoreInstallStatus(StoreInstallPhase.Downloading, 100, 30, 30, null),
            new StoreInstallStatus(StoreInstallPhase.Completed, 100, 100, 100, null),
        ];
        var reports = new SyncProgress();

        await updater.UpdateAsync(reports, CancellationToken.None);

        // the first Install report must come from the Store, after the download reports began
        var firstInstall = reports.Items.FindIndex(p => p.Phase == UpdatePhase.Install);
        Assert.True(firstInstall < 0 || reports.Items.Take(firstInstall).Any(p => p.Phase == UpdatePhase.Download));
        Assert.Contains(reports.Items, p => p.Phase == UpdatePhase.Check && p.Message == "closing Codex");
    }

    private sealed class SyncProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Items { get; } = [];

        public void Report(UpdateProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task CancellingTheRunCancelsTheQueuedStoreItem()
    {
        var (updater, store, appx, _, _) = CreateUpdater();
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");
        store.Handle.StatusSequence = [new StoreInstallStatus(StoreInstallPhase.Downloading, 100, 30, 30, null)];
        using var cts = new CancellationTokenSource();

        var run = updater.UpdateAsync(null, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(store.Handle.CancelCalled);
    }

    [Fact]
    public async Task AStoreUpdateTimeoutCancelsTheQueuedItem()
    {
        var config = DefaultConfig();
        config.Codex.Update.UpdateTimeoutSeconds = 1;
        var appx = new FakeAppxSource();
        var store = new FakeStoreSource();
        var updater = new CodexUpdater(config, appx, store, new FakeProcessSource(), new FakeManifest(), _log);
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");
        store.Handle.StatusSequence = [new StoreInstallStatus(StoreInstallPhase.Downloading, 100, 30, 30, null)]; // never completes

        var ex = await Assert.ThrowsAsync<IOException>(() => updater.UpdateAsync(null, CancellationToken.None));

        Assert.Contains("did not finish within 1 s", ex.Message);
        Assert.True(store.Handle.CancelCalled); // the queue does not keep downloading unattended
    }

    [Fact]
    public async Task StoreErrorFailsTheUpdate()
    {
        var (updater, store, appx, _, _) = CreateUpdater();
        appx.Installed = Package("26.1007.2314.0", "OpenAI.Codex_2p2nqsd0c76g0");
        store.Handle.StatusSequence = [
            new StoreInstallStatus(StoreInstallPhase.Error, 0, 0, 0, "0x80073D02: package in use"),
        ];

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            updater.UpdateAsync(null, CancellationToken.None));

        Assert.Contains("0x80073D02", ex.Message);
    }

    [Fact]
    public async Task UpdateWithoutPackageFamilyNameFails()
    {
        var (updater, _, appx, _, _) = CreateUpdater();
        appx.Installed = Package("26.1007.2314.0", "");

        await Assert.ThrowsAsync<IOException>(() => updater.UpdateAsync(null, CancellationToken.None));
    }

    // --- version manifest ---

    [Fact]
    public void ManifestVersionParsedWhenProductMatches()
    {
        const string json = """
            {"schemaVersion": 1, "buildVersion": "26.803.5235.1", "storeProductId": "9PLM9XGG6VKS", "packageIdentity": "OpenAI.Codex"}
            """;

        Assert.Equal("26.803.5235.1", CodexVersionManifest.ParseVersion(json, "9PLM9XGG6VKS"));
        Assert.Null(CodexVersionManifest.ParseVersion(json, "OTHER-ID"));
    }

    [Fact]
    public void ManifestWithoutVersionIsNotAnError()
    {
        Assert.Null(CodexVersionManifest.ParseVersion("not json", "9PLM9XGG6VKS"));
        Assert.Null(CodexVersionManifest.ParseVersion("{}", "9PLM9XGG6VKS"));
    }

    // --- watchdog probe ---

    [Fact]
    public void ProbeCountsCodexProcessesAndClaudeOnesTogether()
    {
        var probe = new ClaudeUsageProbe(
            new StubProcessFinder([new FoundProcess(1, "cmd"), new FoundProcess(2, "cmd")]),
            new StubConnections(0),
            18081,
            Path.Combine(Path.GetTempPath(), $"pnr-marker-{Guid.NewGuid():N}"),
            codexProcessName: "ChatGPT");

        var reason = probe.GetInUseReason(TimeSpan.FromMinutes(10));

        Assert.Contains("claude processes: 2", reason);
        Assert.Contains("codex processes: 2", reason);
    }

    [Fact]
    public void ProbeWithoutCodexNameKeepsTheOldBehaviour()
    {
        var probe = new ClaudeUsageProbe(
            new StubProcessFinder([new FoundProcess(1, "cmd")]),
            new StubConnections(0),
            18081,
            Path.Combine(Path.GetTempPath(), $"pnr-marker-{Guid.NewGuid():N}"));

        Assert.Equal("claude processes: 1", probe.GetInUseReason(TimeSpan.FromMinutes(10)));
    }

    private (CodexUpdater Updater, FakeStoreSource Store, FakeAppxSource Appx, FakeProcessSource Processes, FakeManifest Manifest) CreateUpdater()
    {
        var config = DefaultConfig();
        var appx = new FakeAppxSource();
        var store = new FakeStoreSource();
        var processes = new FakeProcessSource();
        var manifest = new FakeManifest();
        var updater = new CodexUpdater(config, appx, store, processes, manifest, _log);
        return (updater, store, appx, processes, manifest);
    }

    private static InstalledPackage Package(string version, string familyName) =>
        new("OpenAI.Codex", version, "CN=OpenAI", @"C:\Program Files\WindowsApps\OpenAI.Codex", familyName);

    private sealed class FakePowerShell : IPowerShellExecutor
    {
        public int Calls { get; private set; }

        public string LastScript { get; private set; } = "";

        public int Code { get; set; }

        public string Output { get; set; } = "PNR=ok";

        public Task<(int Code, string Output)> RunScriptAsync(string script, CancellationToken ct)
        {
            Calls++;
            LastScript = script;
            return Task.FromResult((Code, Output));
        }
    }

    private sealed class FakeChain : IChainSupervisor
    {
        public int EnsureCalls { get; private set; }

        public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.FromResult(new ChainState(
            DaemonHttpListening: true, DaemonProcessAlive: true, KeeperRunning: true,
            BackendState: "Running", ProxyRunning: true, ProxyHealthy: true));

        public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct)
        {
            EnsureCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeProcessFinder : IProcessFinder
    {
        public IReadOnlyList<FoundProcess> Processes { get; set; } = [];

        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) => Processes;

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => Processes;

        public FoundProcess? FindById(int processId) => null;
    }

    private sealed class FakeAppxSource : IAppxPackageSource
    {
        public InstalledPackage? Installed { get; set; }

        public InstalledPackage? AfterInstall { get; set; }

        private int _reads;

        public Task<InstalledPackage?> GetInstalledAsync(string packageName, CancellationToken ct)
        {
            var read = _reads++;
            return Task.FromResult(read == 0 ? Installed : AfterInstall ?? Installed);
        }

        public Task AddPackageAsync(string packageFile, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeStoreSource : IStoreUpdateSource
    {
        public bool HasUpdate { get; set; }

        public Exception? Error { get; set; }

        public string? LastProductId { get; private set; }

        public string? LastFamilyName { get; private set; }

        public FakeStoreHandle Handle { get; } = new();

        public Task<bool> HasUpdateAsync(string storeProductId, CancellationToken ct)
        {
            LastProductId = storeProductId;
            return Error is not null ? Task.FromException<bool>(Error) : Task.FromResult(HasUpdate);
        }

        public Task<IStoreInstallHandle> StartUpdateAsync(string packageFamilyName, CancellationToken ct)
        {
            LastFamilyName = packageFamilyName;
            return Task.FromResult<IStoreInstallHandle>(Handle);
        }
    }

    private sealed class FakeStoreHandle : IStoreInstallHandle
    {
        private int _index;

        public StoreInstallStatus[] StatusSequence { get; set; } =
            [new StoreInstallStatus(StoreInstallPhase.Completed, 0, 0, 100, null)];

        public bool CancelCalled { get; private set; }

        public StoreInstallStatus GetStatus()
        {
            var status = StatusSequence[Math.Min(_index, StatusSequence.Length - 1)];
            _index++;
            return status;
        }

        public void Cancel() => CancelCalled = true;
    }

    private sealed class FakeProcessSource : IClaudeProcessSource
    {
        public List<FakeProcessHandle> FindResult { get; set; } = [];

        // once every handle has been asked to close, the app counts as gone (the close loop polls Find)
        public IReadOnlyList<IClaudeProcessHandle> Find(string processName) =>
            FindResult.Count > 0 && FindResult.All(h => h.CloseMainWindowCalled)
                ? []
                : FindResult.Cast<IClaudeProcessHandle>().ToList();
    }

    private sealed class FakeProcessHandle : IClaudeProcessHandle
    {
        public bool CloseMainWindowCalled { get; private set; }

        public bool CloseMainWindow() => CloseMainWindowCalled = true;

        public void Kill()
        {
        }
    }

    private sealed class FakeManifest : ICodexVersionManifest
    {
        public string? Response { get; set; }

        public Task<string?> FindOfferedVersionAsync(CancellationToken ct) =>
            Task.FromResult(Response is null ? null : CodexVersionManifest.ParseVersion(Response, "9PLM9XGG6VKS"));
    }

    private sealed class StubProcessFinder(IReadOnlyList<FoundProcess> processes) : IProcessFinder
    {
        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) => processes;

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => processes;

        public FoundProcess? FindById(int processId) => null;
    }

    private sealed class StubConnections(int count) : IEstablishedConnectionCounter
    {
        public int CountOnLocalPort(int port) => count;
    }

    public void Dispose()
    {
        _log.Dispose();
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }

        if (File.Exists(_envPath))
        {
            File.Delete(_envPath);
        }
    }
}
