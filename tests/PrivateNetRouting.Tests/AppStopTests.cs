using System.Text;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Tests;

/// <summary>
/// The owner's manual app stop: one app at a time, its Windows processes and its hosts in the
/// running configured WSL distros, with the other app left alone.
/// </summary>
public class AppStopTests : IDisposable
{
    private const string ClaudeAlias = @"C:\fake\claude-desktop.exe";
    private const string CodexInstall = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__abc\app\ChatGPT.exe";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-appstop-{Guid.NewGuid():N}.log");
    private readonly FileLog _log;
    private readonly FakeChain _chain = new();
    private readonly FakeProcessFinder _finder = new();
    private readonly FakeWsl _wsl = new();
    private readonly FakeAppCloser _claudeCloser = new();
    private readonly FakeAppCloser _codexCloser = new();
    private readonly AppConfig _config;

    public AppStopTests()
    {
        _log = new FileLog(_logPath);
        _config = new AppConfig
        {
            ForwardProxy = new ForwardProxyConfig { Listen = "127.0.0.1:18081" },
            Claude = new ClaudeConfig { AliasPath = ClaudeAlias },
            Wsl = new WslConfig { Distros = [new WslDistroConfig { Name = "pnr-a" }] },
        };
    }

    /// <summary>A stopper over the real launchers: the tests drive the process scans and the WSL runner through fakes.</summary>
    private AppStopper Create() => new(
        _config,
        new ClaudeLauncher(
            _config,
            _chain,
            _ => Task.FromResult(true),
            _finder,
            _log,
            () => null,
            _ => null,
            _claudeCloser),
        new CodexLauncher(
            _config,
            _chain,
            _ => Task.FromResult(true),
            _finder,
            _log,
            new FakePowerShell(),
            _ => null,
            _codexCloser),
        _wsl);

    /// <summary>The desktop app's own processes: the alias/package marker in the command line is what makes them ours.</summary>
    private void ClaudeRunning(params int[] processIds)
    {
        foreach (var processId in processIds)
        {
            _finder.Processes.Add(new FoundProcess(processId, $"\"{ClaudeAlias}\" --proxy-server=http://127.0.0.1:18081"));
            _claudeCloser.Alive.Add(processId);
        }
    }

    private void CodexRunning(params int[] processIds)
    {
        foreach (var processId in processIds)
        {
            _finder.Processes.Add(new FoundProcess(processId, $"\"{CodexInstall}\" --proxy-server=http://127.0.0.1:18081"));
            _codexCloser.Alive.Add(processId);
        }
    }

    [Fact]
    public async Task StopClaudeClosesItsProcessesAndRunsOnlyTheClaudeWslPass()
    {
        ClaudeRunning(101, 102);
        CodexRunning(201);

        var result = await Create().StopAsync(AppStopTarget.Claude, CancellationToken.None);

        Assert.False(result.Disabled);
        Assert.Equal(2, result.Windows.ClosedGracefully);
        Assert.Equal(2, result.Windows.Scanned);
        Assert.Equal(2, result.Windows.Closed);
        Assert.Empty(result.Windows.Survived);
        Assert.Equal([101, 102], _claudeCloser.GracefulCalls);
        Assert.Empty(_codexCloser.GracefulCalls); // the other app is never touched
        Assert.Equal(2, _wsl.Calls.Count); // listing + one bash pass
        Assert.Contains("STOP_CLAUDE=1", _wsl.LastScript);
        Assert.Contains("STOP_CODEX=0", _wsl.LastScript);
        Assert.Equal(WslHostState.Stopped, result.Wsl?.State);
        Assert.True(result.AnythingStopped);
        Assert.False(result.SomethingSurvived);
    }

    [Fact]
    public async Task StopCodexUsesOnlyTheCodexPass()
    {
        ClaudeRunning(101);
        CodexRunning(201);

        var result = await Create().StopAsync(AppStopTarget.Codex, CancellationToken.None);

        Assert.Equal([201], _codexCloser.GracefulCalls);
        Assert.Empty(_claudeCloser.GracefulCalls);
        Assert.Contains("STOP_CLAUDE=0", _wsl.LastScript);
        Assert.Contains("STOP_CODEX=1", _wsl.LastScript);
        Assert.Equal(WslHostState.Stopped, result.Wsl?.State);
    }

    [Fact]
    public async Task DisabledAppIsNotStoppedAtAll()
    {
        _config.Claude.Enabled = false;
        ClaudeRunning(101);

        var result = await Create().StopAsync(AppStopTarget.Claude, CancellationToken.None);

        Assert.True(result.Disabled);
        Assert.False(result.AnythingStopped);
        Assert.Empty(_claudeCloser.GracefulCalls);
        Assert.Empty(_claudeCloser.KillCalls);
        Assert.Empty(_wsl.Calls);
    }

    [Fact]
    public async Task NothingRunningReportsNothingToStop()
    {
        _wsl.BashResponse = "REMAIN=0\nSTATE=none\n";

        var result = await Create().StopAsync(AppStopTarget.Claude, CancellationToken.None);

        Assert.False(result.Disabled);
        Assert.False(result.AnythingStopped);
        Assert.False(result.SomethingSurvived);
        Assert.Empty(result.Windows.Survived);
        Assert.Equal(WslHostState.None, result.Wsl?.State);
    }

    [Fact]
    public async Task WindowsSurvivorsAndAStuckWslStopAreReported()
    {
        ClaudeRunning(101);
        _claudeCloser.NoWindow.Add(101); // no window to close: the forced kill is the only attempt
        _claudeCloser.SurvivesKill.Add(101);
        _wsl.BashResponse = "REMAIN=1\nSTATE=stuck\n";

        var result = await Create().StopAsync(AppStopTarget.Claude, CancellationToken.None);

        Assert.Equal([101], result.Windows.Survived);
        Assert.Equal(WslHostState.Stuck, result.Wsl?.State);
        Assert.True(result.SomethingSurvived);
    }

    [Fact]
    public async Task WslPassRunsAfterTheWindowsClose()
    {
        ClaudeRunning(101);
        _wsl.ReadCloserCalls = () => _claudeCloser.GracefulCalls.Count;

        await Create().StopAsync(AppStopTarget.Claude, CancellationToken.None);

        // the closing app may restart its WSL host while its own process is still alive: the WSL
        // pass must see the post-close state, so it starts only after the close
        Assert.Equal(1, _wsl.GracefulCallsAtBash);
    }

    [Fact]
    public async Task StopLogsTheOwnerRequestAsTheReason()
    {
        ClaudeRunning(101);

        await Create().StopAsync(AppStopTarget.Claude, CancellationToken.None);

        var log = LogText();
        Assert.Contains("Claude closed on the owner's request", log);
        Assert.DoesNotContain("closed to restart through the proxy", log);
        Assert.Contains("Claude WSL hosts: Stopped", log);
    }

    private string LogText()
    {
        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _log.Dispose();
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    private sealed class FakeChain : IChainSupervisor
    {
        public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.FromResult(new ChainState(
            DaemonHttpListening: true, DaemonProcessAlive: true, KeeperRunning: true,
            BackendState: "Running", ProxyRunning: true, ProxyHealthy: true));

        public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeProcessFinder : IProcessFinder
    {
        public List<FoundProcess> Processes { get; } = [];

        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) => Processes;

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => Processes;

        public FoundProcess? FindById(int processId) => null;
    }

    private sealed class FakePowerShell : IPowerShellExecutor
    {
        public Task<(int Code, string Output)> RunScriptAsync(string script, CancellationToken ct) =>
            Task.FromResult((0, "PNR=ok"));
    }

    /// <summary>The WSL runner: records the bash script so the test sees which pass flags were sent.</summary>
    private sealed class FakeWsl : IWslProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string ListResponse { get; set; } = "pnr-a\r\n";

        public string BashResponse { get; set; } = "REMAIN=0\nSTATE=stopped\n";

        public string LastScript { get; private set; } = "";

        /// <summary>How many Windows close calls had happened when the bash pass started (-1 = not read).</summary>
        public int GracefulCallsAtBash { get; private set; } = -1;

        public Func<int>? ReadCloserCalls { get; set; }

        public Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, Encoding outputEncoding, CancellationToken ct)
        {
            Calls.Add(string.Join(' ', arguments));
            if (arguments[0] == "--list")
            {
                return Task.FromResult(ListResponse);
            }

            LastScript = standardInput ?? "";
            GracefulCallsAtBash = ReadCloserCalls?.Invoke() ?? -1;
            return Task.FromResult(BashResponse);
        }
    }
}
