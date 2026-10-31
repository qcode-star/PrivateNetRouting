using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Tests;

/// <summary>
/// The idle stopper supervisor on fakes: liveness by command lines (the detached worker marker
/// and the `pnr watchdog` CLI form, foreign processes left alone), idempotent start with the
/// marker, confirmation of the start from the process list, the stop killing only the identified
/// pids.
/// </summary>
public class WatchdogSupervisorTests : IDisposable
{
    private readonly string _logDir = Directory.CreateTempSubdirectory("pnr-watchdog-supervisor-").FullName;
    private readonly List<FileLog> _logs = [];

    [Fact]
    public void FindSeesWorkerMarkerAndCliFormAndIgnoresForeign()
    {
        var finder = new FakeFinderByName();
        finder.ByName["PrivateNetRouting.App.exe"] =
        [
            new(11, @"""C:\app\PrivateNetRouting.App.exe"" --watchdog-worker"),
            new(12, @"""C:\app\PrivateNetRouting.App.exe"" --launch"), // the GUI itself, not a stopper
        ];
        finder.ByName["pnr.exe"] =
        [
            new(21, "pnr.exe --watchdog-worker"),
            new(22, "pnr.exe watchdog"), // the foreground CLI form
            new(23, "pnr.exe --proxy-worker"), // another worker, not a stopper
        ];

        Assert.Equal([11, 21, 22], WatchdogSupervisor.FindStopperPids(finder, @"C:\app"));
    }

    /// <summary>A command line that merely mentions "watchdog" is not a stopper.</summary>
    [Fact]
    public void FindIgnoresCommandLinesThatOnlyMentionTheWatchdog()
    {
        var finder = new FakeFinderByName();
        finder.ByName["pnr.exe"] =
        [
            new(31, @"""C:\app\pnr.exe"" logs show watchdog"),
            new(32, @"""C:\app\pnr.exe"" logs show watchdog-worker --tail 20"),
            new(33, @"""C:\app\pnr.exe"" logs show --watchdog-worker"), // the marker, but not as the command
            new(34, @"""C:\app\pnr.exe"" --watchdog-worker"),
            new(35, @"""C:\app\pnr.exe"" watchdog"),
            new(36, @"""C:\watchdog\pnr.exe"" status"), // the word in the path only
        ];
        finder.ByName["PrivateNetRouting.App.exe"] =
        [
            new(41, @"""C:\watchdog\PrivateNetRouting.App.exe"" --launch"),
            new(42, @"""C:\app\PrivateNetRouting.App.exe"" --config ""C:\x\--watchdog-worker.json"""),
            new(43, @"""C:\app\PrivateNetRouting.App.exe"" --watchdog-worker"),
        ];

        Assert.Equal([43, 34, 35], WatchdogSupervisor.FindStopperPids(finder, @"C:\app"));
    }

    /// <summary>Another installation's worker carries the same image name and marker — it is not ours to kill.</summary>
    [Fact]
    public void FindIgnoresAnotherInstallationsStopper()
    {
        var finder = new FakeFinderByName();
        finder.ByName["PrivateNetRouting.App.exe"] =
        [
            new(51, @"""C:\other-install\PrivateNetRouting.App.exe"" --watchdog-worker"),
            new(52, @"""C:\app\PrivateNetRouting.App.exe"" --watchdog-worker"),
        ];

        Assert.Equal([52], WatchdogSupervisor.FindStopperPids(finder, @"C:\app"));
    }

    [Fact]
    public async Task StopRechecksTheIdentityBeforeTheKill()
    {
        var killed = new List<int>();
        var log = new FileLog(Path.Combine(_logDir, $"{Guid.NewGuid():N}.log"));
        _logs.Add(log);
        var supervisor = new WatchdogSupervisor(
            new WatchdogConfig { WorkerStopWaitSeconds = 0 },
            new VanishingFinder(),
            log,
            spawnWorker: (_, _, _) => 0,
            killProcess: killed.Add,
            currentExePath: () => @"C:\app\PrivateNetRouting.App.exe",
            delay: (_, _) => Task.CompletedTask);

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Empty(killed); // the pid vanished before the kill — nobody is killed blindly
    }

    /// <summary>A stopper visible only on the first lookup: it is gone by the time the kill runs.</summary>
    private sealed class VanishingFinder : IProcessFinder
    {
        private int _calls;

        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains)
        {
            _calls++;
            return _calls == 1
                ? [new FoundProcess(71, @"""C:\app\PrivateNetRouting.App.exe"" --watchdog-worker")]
                : [];
        }

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => [];

        public FoundProcess? FindById(int processId) => null;
    }

    [Fact]
    public void SplitArgumentsFollowsTheWindowsRules()
    {
        Assert.Equal(
            [@"C:\Program Files\app\pnr.exe", "logs", "show", "a b", @"x\""y", @"c:\dir\"],
            WatchdogSupervisor.SplitArguments(@"""C:\Program Files\app\pnr.exe"" logs  show ""a b"" x\\\""y ""c:\dir\\"""));
        Assert.Equal(["pnr.exe"], WatchdogSupervisor.SplitArguments("pnr.exe"));
    }

    [Fact]
    public void NothingRunningWhenNoStopperProcess()
    {
        var finder = new FakeFinderByName();
        finder.ByName["pnr.exe"] = [new(23, "pnr.exe --proxy-worker")];

        Assert.Empty(WatchdogSupervisor.FindStopperPids(finder, @"C:\app"));
    }

    [Fact]
    public async Task StartSpawnsMarkerAndWaitsUntilTheProcessListConfirms()
    {
        var finder = new FakeFinderByName();
        var spawned = new List<string>();
        var supervisor = Create(finder, spawn: commandLine =>
        {
            spawned.Add(commandLine);
            finder.ByName["PrivateNetRouting.App.exe"] = [new(33, commandLine)];
        });

        await supervisor.StartAsync(CancellationToken.None);

        var commandLine = Assert.Single(spawned);
        Assert.Contains(WatchdogSupervisor.WorkerMarker, commandLine);
        Assert.Equal([33], supervisor.FindStopperPids());
    }

    [Fact]
    public async Task StartIsIdempotentWhileAStopperIsRunning()
    {
        var finder = new FakeFinderByName();
        finder.ByName["pnr.exe"] = [new(22, "pnr.exe watchdog")];
        var spawned = new List<string>();
        var supervisor = Create(finder, spawn: spawned.Add);

        await supervisor.StartAsync(CancellationToken.None);

        Assert.Empty(spawned);
        Assert.Equal([22], supervisor.FindStopperPids());
    }

    /// <summary>
    /// Two starts close together (the GUI and a CLI raise): the second one must wait for the first
    /// spawn to become visible in the process list instead of seeing "no stopper" and spawning again.
    /// </summary>
    [Fact]
    public async Task ConcurrentStartsSpawnASingleWorker()
    {
        var finder = new FakeFinderByName();
        var spawned = 0;
        using var firstInside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Create(finder, spawn: commandLine =>
        {
            spawned++;
            firstInside.Set();
            release.Wait(TimeSpan.FromSeconds(10)); // the worker is not in the process list yet
            finder.ByName["PrivateNetRouting.App.exe"] = [new(33, commandLine)];
        });
        var second = Create(finder, spawn: _ => spawned++);

        var firstStart = Task.Run(() => first.StartAsync(CancellationToken.None));
        Assert.True(firstInside.Wait(TimeSpan.FromSeconds(10)));
        var secondStart = Task.Run(() => second.StartAsync(CancellationToken.None));
        await Task.Delay(300); // the second start is parked on the lock, not past the check
        release.Set();
        await Task.WhenAll(firstStart, secondStart);

        Assert.Equal(1, spawned);
    }

    /// <summary>
    /// A raise followed by a quick stop: the stop waits for the pending auto start, so the worker
    /// it spawns is already visible (and killed) instead of surviving over the stopped chain.
    /// </summary>
    [Fact]
    public async Task StopWaitsForAPendingAutoStartAndKillsItsWorker()
    {
        var finder = new FakeFinderByName();
        var killed = new List<int>();
        using var spawning = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var supervisor = Create(
            finder,
            spawn: commandLine =>
            {
                spawning.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                finder.ByName["PrivateNetRouting.App.exe"] = [new(33, commandLine)];
            },
            kill: pid =>
            {
                killed.Add(pid);
                finder.ByName["PrivateNetRouting.App.exe"] = [];
            });

        supervisor.TrackAutoStart();
        Assert.True(spawning.Wait(TimeSpan.FromSeconds(10)));
        var stop = supervisor.StopAsync(CancellationToken.None); // the worker is not visible yet
        await Task.Delay(200);
        Assert.False(stop.IsCompleted); // it waits for the spawn to settle
        release.Set();
        await stop;

        Assert.Equal([33], killed);
    }

    [Fact]
    public void WorkerRefusesToRunBesideALowerPidStopperOrOverAStoppedChain()
    {
        var up = new ChainState(true, true, true, "Running", true, true);
        var stopped = new ChainState(false, false, false, null, false, false);

        Assert.Null(WatchdogWorker.RefusalReason([50], 50, up)); // alone
        Assert.Null(WatchdogWorker.RefusalReason([50, 60], 50, up)); // the lowest pid stays
        Assert.NotNull(WatchdogWorker.RefusalReason([50, 60], 60, up)); // the higher one steps aside
        Assert.NotNull(WatchdogWorker.RefusalReason([50], 50, stopped)); // nothing to watch
    }

    [Fact]
    public async Task StartThrowsWhenTheWorkerNeverAppears()
    {
        var config = new WatchdogConfig { WorkerStartWaitSeconds = 0 }; // the deadline is already behind
        var supervisor = Create(new FakeFinderByName(), config: config, spawn: _ => { });

        await Assert.ThrowsAsync<IOException>(() => supervisor.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopKillsOnlyTheIdentifiedStoppers()
    {
        var finder = new FakeFinderByName();
        var killed = new List<int>();
        var supervisor = Create(finder, kill: pid =>
        {
            killed.Add(pid);
            // the killed stopper disappears from the process list
            finder.ByName["PrivateNetRouting.App.exe"] = [];
            finder.ByName["pnr.exe"] = finder.ByName["pnr.exe"].Where(p => p.Id != pid).ToList();
        });
        finder.ByName["PrivateNetRouting.App.exe"] = [new(11, @"""C:\app\PrivateNetRouting.App.exe"" --watchdog-worker")];
        finder.ByName["pnr.exe"] =
        [
            new(22, "pnr.exe watchdog"),
            new(23, "pnr.exe --proxy-worker"),
        ];

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal([11, 22], killed);
    }

    [Fact]
    public async Task StopWithoutStoppersKillsNothing()
    {
        var killed = new List<int>();
        var supervisor = Create(new FakeFinderByName(), kill: killed.Add);

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Empty(killed);
    }

    /// <summary>
    /// The calling stopper is left alone: the worker reaches the stop through its own chain stop
    /// (WatchdogService → the orchestrator's chain-stopped hook) and exits right after — killing
    /// its own pid here would cut that exit short. Other stoppers still go.
    /// </summary>
    [Fact]
    public async Task StopLeavesTheCallingStopperAlone()
    {
        var self = Environment.ProcessId;
        var finder = new FakeFinderByName();
        finder.ByName["PrivateNetRouting.App.exe"] =
        [
            new(self, @"""C:\app\PrivateNetRouting.App.exe"" --watchdog-worker"), // this process itself
            new(11, @"""C:\app\PrivateNetRouting.App.exe"" --watchdog-worker"),
        ];
        var killed = new List<int>();
        var waits = 0;
        var supervisor = Create(
            finder,
            kill: pid =>
            {
                killed.Add(pid);
                finder.ByName["PrivateNetRouting.App.exe"] = finder.ByName["PrivateNetRouting.App.exe"]
                    .Where(p => p.Id != pid)
                    .ToList();
            },
            delay: () => waits++);

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal([11], killed); // the worker's own pid is not in the kill list
        Assert.Equal(0, waits); // nor in the wait for the killed ones to disappear
        Assert.Contains(self, supervisor.FindStopperPids()); // it exits by itself right after
    }

    [Fact]
    public async Task KillFailureIsLoggedAndTheRestAreStopped()
    {
        var finder = new FakeFinderByName();
        finder.ByName["pnr.exe"] =
        [
            new(21, "pnr.exe watchdog"),
            new(22, "pnr.exe --watchdog-worker"),
        ];
        var killed = new List<int>();
        var config = new WatchdogConfig { WorkerStopWaitSeconds = 0 }; // 21 is unkillable, do not wait for it
        var supervisor = Create(finder, config: config, kill: pid =>
        {
            if (pid == 21)
            {
                throw new InvalidOperationException("process has exited");
            }

            killed.Add(pid);
            finder.ByName["pnr.exe"] = [new(21, "pnr.exe watchdog")];
        });

        await supervisor.StopAsync(CancellationToken.None); // the failure is not an error of the stop

        Assert.Equal([22], killed);
    }

    [Fact]
    public async Task AutoStartRespectsTheConfigFlagAndARunningStopper()
    {
        var finder = new FakeFinderByName();
        finder.ByName["pnr.exe"] = [new(22, "pnr.exe watchdog")];
        var spawned = new List<string>();

        var off = Create(finder, config: new WatchdogConfig { AutoStart = false, WorkerStartWaitSeconds = 0 }, spawn: spawned.Add);
        await off.AutoStartAsync(CancellationToken.None);
        Assert.Empty(spawned); // the flag is off — nothing spawns

        var running = Create(finder, config: new WatchdogConfig { WorkerStartWaitSeconds = 0 }, spawn: spawned.Add);
        await running.AutoStartAsync(CancellationToken.None);
        Assert.Empty(spawned); // a stopper is already running — nothing spawns

        var absent = new FakeFinderByName();
        var armed = Create(absent, config: new WatchdogConfig { WorkerStartWaitSeconds = 0 }, spawn: commandLine =>
        {
            spawned.Add(commandLine);
            absent.ByName["PrivateNetRouting.App.exe"] = [new(33, commandLine)];
        });
        await armed.AutoStartAsync(CancellationToken.None);
        Assert.Single(spawned); // off and absent — armed
    }

    [Fact]
    public async Task AutoStartFailureIsSwallowed()
    {
        var config = new WatchdogConfig { WorkerStartWaitSeconds = 0 }; // the worker never appears — a failure
        var supervisor = Create(new FakeFinderByName(), config: config, spawn: _ => { });

        await supervisor.AutoStartAsync(CancellationToken.None); // no throw: the raise must not fail because of it
    }

    private WatchdogSupervisor Create(
        FakeFinderByName finder,
        WatchdogConfig? config = null,
        Action<string>? spawn = null,
        Action<int>? kill = null,
        Action? delay = null)
    {
        // a personal log path per supervisor: several FileLog writers on one file are not allowed
        var log = new FileLog(Path.Combine(_logDir, $"{Guid.NewGuid():N}.log"));
        _logs.Add(log);
        return new WatchdogSupervisor(
            config ?? new WatchdogConfig(),
            finder,
            log,
            spawnWorker: (commandLine, _, _) =>
            {
                spawn?.Invoke(commandLine);
                return 0;
            },
            killProcess: kill ?? (_ => { }),
            currentExePath: () => @"C:\app\PrivateNetRouting.App.exe",
            delay: (_, _) =>
            {
                delay?.Invoke();
                return Task.CompletedTask;
            });
    }

    public void Dispose()
    {
        foreach (var log in _logs)
        {
            log.Dispose();
        }

        Directory.Delete(_logDir, recursive: true);
    }
}
