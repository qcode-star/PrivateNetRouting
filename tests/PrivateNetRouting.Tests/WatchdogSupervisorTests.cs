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

        Assert.Equal([11, 21, 22], WatchdogSupervisor.FindStopperPids(finder));
    }

    [Fact]
    public void NothingRunningWhenNoStopperProcess()
    {
        var finder = new FakeFinderByName();
        finder.ByName["pnr.exe"] = [new(23, "pnr.exe --proxy-worker")];

        Assert.Empty(WatchdogSupervisor.FindStopperPids(finder));
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
        var supervisor = Create(finder, kill: pid =>
        {
            killed.Add(pid);
            finder.ByName["PrivateNetRouting.App.exe"] = finder.ByName["PrivateNetRouting.App.exe"]
                .Where(p => p.Id != pid)
                .ToList();
        });

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal([11], killed); // the worker's own pid is not in the kill list
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
        Action<int>? kill = null)
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
            delay: (_, _) => Task.CompletedTask);
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
