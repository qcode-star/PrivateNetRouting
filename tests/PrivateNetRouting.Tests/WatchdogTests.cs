using System.Text;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Idle-stop watchdog on fakes: the "WSL host → chain" order, skipping WSL without a distro,
/// waiting for the interval before the first check, the stopper output parser and usage-probe semantics.
/// </summary>
public class WatchdogTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-watchdog-{Guid.NewGuid():N}.log");
    private readonly List<FileLog> _logs = [];

    [Fact]
    public async Task IdleStopsWslHostBeforeChain()
    {
        var events = new List<string>();
        var wsl = new FakeWsl(events);
        var chain = new FakeChain(events);
        var watchdog = Create(usage: new FakeUsage(null), wsl: wsl, chain: chain);

        await watchdog.RunAsync(null, CancellationToken.None);

        Assert.Equal(["wsl.stop:pnr-test", "chain.stop"], events);
    }

    [Fact]
    public async Task FirstCheckHappensAfterInterval()
    {
        var events = new List<string>();
        var usage = new FakeUsage(null, events: events);
        var watchdog = Create(usage: usage, wsl: new FakeWsl(events), chain: new FakeChain(events), events: events);

        await watchdog.RunAsync(null, CancellationToken.None);

        Assert.Equal(["delay", "usage.check", "wsl.stop:pnr-test", "chain.stop"], events);
    }

    [Fact]
    public async Task InUseKeepsLoopThenIdleStops()
    {
        var events = new List<string>();
        var usage = new FakeUsage("claude processes: 3", thenNull: true, events: events);
        var chain = new FakeChain(events);
        var watchdog = Create(usage: usage, wsl: new FakeWsl(events), chain: chain, events: events);

        await watchdog.RunAsync(null, CancellationToken.None);

        Assert.Equal(2, usage.Checks);
        Assert.Equal(["delay", "usage.check", "delay", "usage.check", "wsl.stop:pnr-test", "chain.stop"], events);
    }

    [Fact]
    public async Task MissingWslDistroSkipsWslStop()
    {
        var events = new List<string>();
        var chain = new FakeChain(events);
        var watchdog = Create(usage: new FakeUsage(null), wsl: null, chain: chain);

        await watchdog.RunAsync(null, CancellationToken.None);

        Assert.Equal(["chain.stop"], events);
    }

    [Fact]
    public async Task FailedChainStopIsRetriedOnTheNextTick()
    {
        var events = new List<string>();
        var chain = new FakeChain(events)
        {
            StopThrows = new ChainException("daemon", new IOException("UAC declined")),
            StopFailures = 1,
        };
        var watchdog = Create(usage: new FakeUsage(null), wsl: new FakeWsl(events), chain: chain);

        await watchdog.RunAsync(null, CancellationToken.None);

        // the declined stop does not end the stopper: the second tick stops the chain and exits
        Assert.Equal(["wsl.stop:pnr-test", "chain.stop", "wsl.stop:pnr-test", "chain.stop"], events);
    }

    [Fact]
    public async Task ThrowingProbeDoesNotKillTheLoop()
    {
        var events = new List<string>();
        var usage = new FakeUsage(null, throwOnFirst: true);
        var watchdog = Create(usage: usage, wsl: null, chain: new FakeChain(events));

        await watchdog.RunAsync(null, CancellationToken.None);

        Assert.Equal(["chain.stop"], events);
        Assert.Equal(2, usage.Checks);
    }

    [Fact]
    public async Task DistroNotRunningSkipsBash()
    {
        var wsl = new FakeWsl([]) { ListResponse = "Ubuntu\r\n" };
        var stopper = new WslAppHostStopper(["pnr-test"], stopClaude: true, stopCodex: true, wsl);

        var result = await stopper.StopAsync(CancellationToken.None);

        Assert.Equal(WslHostState.None, result.State);
        Assert.Contains("no configured distro is running", result.Detail);
        Assert.Single(wsl.Calls); // bash was not started; the distro is not brought up just for the check
    }

    [Fact]
    public async Task DistroListingNulsAreStripped()
    {
        var wsl = new FakeWsl([]) { ListResponse = "pnr\0-test\r\n" }; // UTF-16LE-like junk
        var stopper = new WslAppHostStopper(["pnr-test"], stopClaude: true, stopCodex: true, wsl);

        await stopper.StopAsync(CancellationToken.None);

        Assert.Equal(2, wsl.Calls.Count); // listing + bash stop
    }

    [Fact]
    public void ScriptInputIsNormalizedToLf()
    {
        // raw string literals capture the source line endings; bash dies on CR
        Assert.Equal("a\nb\nc\n", WslProcessRunner.NormalizeScript("a\r\nb\rc\n"));
        Assert.Equal("a\nb\n", WslProcessRunner.NormalizeScript("a\nb\n"));
    }

    [Theory]
    [InlineData("STATE=none", WslHostState.None)]
    [InlineData("STATE=stopped", WslHostState.Stopped)]
    [InlineData("STATE=stuck", WslHostState.Stuck)]
    [InlineData("STOP pid=7 -> ok\nREMAIN=0\nSTATE=stopped", WslHostState.Stopped)]
    [InlineData("wsl: some error", WslHostState.Stuck)]
    public void ParseReadsState(string output, WslHostState expected) =>
        Assert.Equal(expected, WslAppHostStopper.Parse(output).State);

    [Fact]
    public void ParseKeepsDetailWithoutStateLine()
    {
        var result = WslAppHostStopper.Parse("wsl: error\r\n");
        Assert.Equal(WslHostState.Stuck, result.State);
        Assert.Contains("no STATE", result.Detail);
    }

    /// <summary>The stop script semantics: identifying the daemon/sessions,
    /// RPC shutdown with its own binary, SIGTERM → SIGKILL escalation with a wait.</summary>
    [Fact]
    public void StopScriptPortsSourceSemantics()
    {
        Assert.Contains(@"\/\.claude\/remote\/srv\/[^/]+\/server$", WslAppHostStopper.Script(true, true));
        Assert.Contains(@"\/\.claude\/remote\/ccd-cli\/", WslAppHostStopper.Script(true, true));
        Assert.Contains("--stop --socket", WslAppHostStopper.Script(true, true));
        Assert.Contains("kill -TERM", WslAppHostStopper.Script(true, true));
        Assert.Contains("kill -KILL", WslAppHostStopper.Script(true, true));
        Assert.Contains("wait_empty 15", WslAppHostStopper.Script(true, true)); // wait after RPC
        Assert.Contains("wait_empty 10", WslAppHostStopper.Script(true, true)); // wait after SIGTERM
        Assert.Contains("STATE=", WslAppHostStopper.Script(true, true));
    }

    /// <summary>The Codex pass keys on the desktop app's signature — CODEX_HOME in the
    /// process environment — and never names a bare codex process.</summary>
    [Fact]
    public void StopScriptTargetsCodexByCodexHomeEnvOnly()
    {
        var script = WslAppHostStopper.Script(true, true);
        Assert.Contains("CODEX_HOME=*)", script);
        Assert.Contains("< \"$p/environ\"", script);
        Assert.DoesNotContain("pgrep codex", script);
        Assert.DoesNotContain("pkill codex", script);
    }

    /// <summary>The app gates are baked into the script: an app disabled in the config
    /// leaves its whole pass inert.</summary>
    [Fact]
    public void StopScriptGatesTheAppPassesByFlags()
    {
        var both = WslAppHostStopper.Script(true, true);
        Assert.Contains("STOP_CLAUDE=1", both);
        Assert.Contains("STOP_CODEX=1", both);

        var claudeOnly = WslAppHostStopper.Script(stopClaude: true, stopCodex: false);
        Assert.Contains("STOP_CLAUDE=1", claudeOnly);
        Assert.Contains("STOP_CODEX=0", claudeOnly);

        var neither = WslAppHostStopper.Script(false, false);
        Assert.Contains("STOP_CLAUDE=0", neither);
        Assert.Contains("STOP_CODEX=0", neither);
    }

    /// <summary>Every RUNNING configured distro is visited; a distro that is not running
    /// is not started for the stop. The aggregate state is the worst of the distros.</summary>
    [Fact]
    public async Task StopperVisitsEveryRunningConfiguredDistroAndAggregates()
    {
        var wsl = new FakeWsl([]) { ListResponse = "Ubuntu\r\npnr-a\r\npnr-b\r\n" };
        var stopper = new WslAppHostStopper(["pnr-a", "pnr-b", "pnr-c"], stopClaude: true, stopCodex: true, wsl);

        var result = await stopper.StopAsync(CancellationToken.None);

        Assert.Equal(WslHostState.Stopped, result.State);
        Assert.Equal(3, wsl.Calls.Count); // listing + two bash stops; pnr-c is not running
        Assert.Contains("[pnr-a]", result.Detail);
        Assert.Contains("[pnr-b]", result.Detail);
        Assert.DoesNotContain("[pnr-c]", result.Detail);
    }

    /// <summary>A wedged distro must not hold the idle stop (and the chain stop behind it) forever.</summary>
    [Fact]
    public async Task StopperTimesOutAWedgedDistroAndMovesOn()
    {
        var wsl = new HangingWsl("pnr-a", "pnr-a\r\npnr-b\r\n");
        var stopper = new WslAppHostStopper(
            ["pnr-a", "pnr-b"], stopClaude: true, stopCodex: true, wsl, distroTimeout: TimeSpan.FromMilliseconds(200));

        var result = await stopper.StopAsync(CancellationToken.None);

        Assert.Equal(WslHostState.Stuck, result.State);
        Assert.Contains("[pnr-a] Stuck: timed out", result.Detail);
        Assert.Contains("[pnr-b] Stopped", result.Detail); // the next distro was still visited
    }

    [Fact]
    public async Task StopperDoesNotSwallowTheCallersCancellation()
    {
        var wsl = new HangingWsl("pnr-a", "pnr-a\r\n");
        var stopper = new WslAppHostStopper(["pnr-a"], stopClaude: true, stopCodex: true, wsl);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopper.StopAsync(cts.Token));
    }

    [Fact]
    public void StopScriptBoundsTheClaudeRpcStop()
    {
        Assert.Contains("timeout 20 \"$exe\" --stop", WslAppHostStopper.Script(stopClaude: true, stopCodex: false));
    }

    /// <summary>wsl.exe that never returns for the hanging distro and answers STATE=stopped for the others.</summary>
    private sealed class HangingWsl(string hangingDistro, string listing) : IWslProcessRunner
    {
        public async Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, Encoding outputEncoding, CancellationToken ct)
        {
            if (arguments[0] == "--list")
            {
                return listing;
            }

            if (arguments[1] == hangingDistro)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return "STOP pid=1 -> ok\nREMAIN=0\nSTATE=stopped\n";
        }
    }

    [Fact]
    public void UsageProbePriorityProcessesOverConnectionsOverMarker()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"pnr-marker-{Guid.NewGuid():N}");
        File.WriteAllText(marker, "x");
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - TimeSpan.FromSeconds(30));
        var probe = new ClaudeUsageProbe(
            new FakeFinder(2),
            new FakeCounter(5),
            proxyPort: 18082,
            activityMarkerPath: marker);

        Assert.Equal("claude processes: 2", probe.GetInUseReason(TimeSpan.FromMinutes(10)));

        probe = new ClaudeUsageProbe(new FakeFinder(0), new FakeCounter(5), 18082, marker);
        Assert.Equal("established connections to 18082: 5", probe.GetInUseReason(TimeSpan.FromMinutes(10)));

        probe = new ClaudeUsageProbe(new FakeFinder(0), new FakeCounter(0), 18082, marker);
        Assert.StartsWith("proxy activity", probe.GetInUseReason(TimeSpan.FromMinutes(10)));
        Assert.Null(probe.GetInUseReason(TimeSpan.FromSeconds(10))); // the marker is older than the window, so idle
    }

    [Fact]
    public void UsageProbeFailsClosedWhenASourceFails()
    {
        var probe = new ClaudeUsageProbe(
            new ThrowingFinder(),
            new FakeCounter(1),
            proxyPort: 18082,
            activityMarkerPath: "Z:\\definitely\\missing");

        Assert.Equal("established connections to 18082: 1", probe.GetInUseReason(TimeSpan.FromMinutes(10)));

        // a failed source with nothing positive elsewhere is not an idle verdict: the chain stays up
        probe = new ClaudeUsageProbe(new ThrowingFinder(), new FakeCounter(0), 18082, "Z:\\definitely\\missing");
        Assert.StartsWith("probe failed", probe.GetInUseReason(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void UsageProbeCountsTheEgressListenerOfTheBrowserProfile()
    {
        // The browser profile goes straight to the chain's egress, past the application proxy:
        // its live connections must keep the chain from being stopped under an open browser.
        var ports = new Dictionary<int, int> { [18081] = 0, [18083] = 3 };
        var probe = new ClaudeUsageProbe(
            new FakeFinder(0),
            new PortsCounter(ports),
            proxyPort: 18081,
            activityMarkerPath: "Z:\\definitely\\missing",
            codexProcessName: "",
            egressPort: 18083);

        Assert.Equal("established connections to the egress 18083: 3", probe.GetInUseReason(TimeSpan.FromMinutes(10)));

        // Without an egress port (the socks5 mode: stopping the chain does not cut the browser)
        // the same connections mean nothing.
        var withoutEgress = new ClaudeUsageProbe(
            new FakeFinder(0),
            new PortsCounter(ports),
            proxyPort: 18081,
            activityMarkerPath: "Z:\\definitely\\missing");
        Assert.Null(withoutEgress.GetInUseReason(TimeSpan.FromMinutes(10)));
    }

    /// <summary>
    /// The skipped socks5 shape: no local proxy exists, nobody touches the marker — the signal
    /// is skipped (a fresh marker file on disk must not look like activity), while connections
    /// to the external endpoint's port still count.
    /// </summary>
    [Fact]
    public void UsageProbeWithoutMarkerCountsOnlyConnections()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"pnr-marker-{Guid.NewGuid():N}");
        File.WriteAllText(marker, "x"); // fresh mtime — would read as activity if consulted
        try
        {
            var ports = new Dictionary<int, int> { [7080] = 4 };
            var probe = new ClaudeUsageProbe(
                new FakeFinder(0),
                new PortsCounter(ports),
                proxyPort: 7080,
                activityMarkerPath: null);

            Assert.Equal("established connections to 7080: 4", probe.GetInUseReason(TimeSpan.FromMinutes(10)));

            var idle = new ClaudeUsageProbe(
                new FakeFinder(0),
                new PortsCounter(new Dictionary<int, int> { [7080] = 0 }),
                proxyPort: 7080,
                activityMarkerPath: null);
            Assert.Null(idle.GetInUseReason(TimeSpan.FromMinutes(10)));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void StopScriptNeverTargetsItselfOrItsAncestors()
    {
        var script = WslAppHostStopper.Script(stopClaude: true, stopCodex: true);

        Assert.Contains("q=$$", script);
        Assert.Contains("case \"$self_pids\" in *\" $pid \"*) continue ;; esac", script);
        Assert.DoesNotContain("\r", script);
    }

    /// <summary>
    /// /proc/&lt;pid&gt;/environ is the exec-time environment: a forked subshell of the script shows
    /// the inherited CODEX_HOME, so a scan wrapped in $(…) or a pipeline would count itself and
    /// never reach 0. The variable is dropped up front and the scan is called directly.
    /// </summary>
    [Fact]
    public void StopScriptScansCodexWithoutForkedCopiesOfItself()
    {
        var script = WslAppHostStopper.Script(stopClaude: false, stopCodex: true);

        var unset = script.IndexOf("unset CODEX_HOME", StringComparison.Ordinal);
        Assert.True(unset >= 0);
        Assert.True(unset < script.IndexOf("codex_scan() {", StringComparison.Ordinal));
        Assert.DoesNotContain("$(codex", script);
        Assert.DoesNotContain("codex_scan |", script);
        Assert.Contains("read -r -d '' entry", script);
        Assert.Contains("kill -TERM $CODEX_PIDS", script);
        Assert.Contains("kill -KILL $CODEX_PIDS", script);
        Assert.DoesNotContain("\r", script);
    }

    private WatchdogService Create(
        FakeUsage usage,
        FakeWsl? wsl,
        FakeChain chain,
        List<string>? events = null)
    {
        var log = new FileLog(_logPath);
        _logs.Add(log);
        IWslAppHost? host = wsl is null ? null : new WslAppHostStopper(["pnr-test"], stopClaude: true, stopCodex: true, wsl);
        return new WatchdogService(
            new WatchdogConfig { CheckSeconds = 600 },
            usage,
            host,
            chain,
            log,
            delay: (_, ct) =>
            {
                events?.Add("delay");
                return Task.CompletedTask;
            });
    }

    protected virtual void Dispose(bool disposing)
    {
        foreach (var log in _logs)
        {
            log.Dispose();
        }

        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeUsage(string? reason, bool thenNull = false, List<string>? events = null, bool throwOnFirst = false) : IClaudeUsageProbe
    {
        public int Checks { get; private set; }

        public string? GetInUseReason(TimeSpan idleWindow)
        {
            Checks++;
            events?.Add("usage.check");
            if (throwOnFirst && Checks == 1)
            {
                throw new UnauthorizedAccessException("marker denied");
            }

            if (thenNull && Checks > 1)
            {
                return null;
            }

            return reason;
        }
    }

    private sealed class FakeWsl(List<string> events) : IWslProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string ListResponse { get; set; } = "pnr-test\r\n";

        public string BashResponse { get; set; } = "STOP pid=1 -> ok\nREMAIN=0\nSTATE=stopped\n";

        public Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, Encoding outputEncoding, CancellationToken ct)
        {
            Calls.Add(string.Join(' ', arguments));
            if (arguments[0] == "--list")
            {
                return Task.FromResult(ListResponse);
            }

            events.Add("wsl.stop:" + arguments[1]);
            // root: the app hosts of any user must be visible and signalable
            Assert.Equal(["-d", "-u", "root", "--", "bash", "-s"], [arguments[0], arguments[2], arguments[3], arguments[4], arguments[5], arguments[6]]);
            return Task.FromResult(BashResponse);
        }
    }

    private sealed class FakeChain(List<string> events) : IChainSupervisor
    {
        public ChainException? StopThrows { get; set; }

        /// <summary>How many stops throw before they start succeeding; 0 — every stop throws.</summary>
        public int StopFailures { get; set; }

        private int _stopCalls;

        public Task<ChainState> GetStateAsync(CancellationToken ct) => Task.FromResult(new ChainState(false, false, false, null, false, false));

        public Task EnsureRunningAsync(IProgress<ChainStep>? progress, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(IProgress<ChainStep>? progress, CancellationToken ct)
        {
            events.Add("chain.stop");
            _stopCalls++;
            if (StopThrows is not null && (StopFailures == 0 || _stopCalls <= StopFailures))
            {
                throw StopThrows;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeFinder(int count) : IProcessFinder
    {
        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) =>
            Enumerable.Range(0, count).Select(i => new FoundProcess(i, "")).ToList();

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) =>
            Enumerable.Range(0, count).Select(i => new FoundProcess(i, "")).ToList();

        public FoundProcess? FindById(int processId) => null;
    }

    private sealed class ThrowingFinder : IProcessFinder
    {
        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) => throw new System.Management.ManagementException("wmi down");

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => throw new System.Management.ManagementException("wmi down");

        public FoundProcess? FindById(int processId) => throw new System.Management.ManagementException("wmi down");
    }

    private sealed class FakeCounter(int count) : IEstablishedConnectionCounter
    {
        public int CountOnLocalPort(int port) => count;
    }

    /// <summary>Connection counts per local port — the probe asks about the proxy and the egress separately.</summary>
    private sealed class PortsCounter(Dictionary<int, int> counts) : IEstablishedConnectionCounter
    {
        public int CountOnLocalPort(int port) => counts.TryGetValue(port, out var count) ? count : 0;
    }
}
