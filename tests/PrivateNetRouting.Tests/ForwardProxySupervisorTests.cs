using System.Net;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

// ----- proxy supervisor fakes -----

internal sealed class FakePortProbe : IPortProbe
{
    public bool Listening;

    public bool IsListening(int port) => Listening;
}

internal sealed class FakeProxyInstance : IForwardProxyInstance
{
    public int StartCount;

    public int StopCount;

    public bool IsRunning { get; set; }

    public IReadOnlyList<string> ListenerEndpoints { get; set; } = [];

    public int ActiveConnections { get; set; }

    public long TotalConnections { get; set; }

    public long FailedConnections { get; set; }

    public Task StartAsync(CancellationToken ct)
    {
        StartCount++;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }
}

/// <summary>Lookup by PID: canned command lines (Find/FindByNameContains are empty).</summary>
internal sealed class FakeFinderById : IProcessFinder
{
    public Dictionary<int, string> CommandLineById { get; } = [];

    public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) => [];

    public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => [];

    public FoundProcess? FindById(int processId) =>
        CommandLineById.TryGetValue(processId, out var commandLine)
            ? new FoundProcess(processId, commandLine)
            : null;
}

public sealed class ForwardProxySupervisorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pnr-supervisor-" + Guid.NewGuid().ToString("N"));

    public ForwardProxySupervisorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static TcpListenerEntry Entry(int pid, string? name = null, IPAddress? address = null) =>
        new(address ?? IPAddress.Loopback, 18082, pid, name);

    private static readonly IPAddress WslGateway = IPAddress.Parse("10.0.2.2");

    private ForwardProxySupervisor Make(
        ForwardProxyConfig config,
        FakeProxyInstance inner,
        FakePortProbe ports,
        FakeTcpTable tcp,
        FakeFinderById processes,
        Func<string, string?, string, int?>? spawn = null,
        Action<int>? kill = null,
        string exePath = @"C:\app\pnr.exe",
        IProxyStopChannel? channel = null) =>
        new(
            config,
            inner,
            ports,
            tcp,
            processes,
            new FileLog(Path.Combine(_tempDir, $"supervisor-{Guid.NewGuid():N}.log")),
            spawn,
            kill,
            () => exePath,
            stopChannel: channel);

    [Fact]
    public async Task Start_InProcess_WhenDetachedOffAndPortFree()
    {
        var inner = new FakeProxyInstance();
        var supervisor = Make(new ForwardProxyConfig(), inner, new FakePortProbe(), new FakeTcpTable(), new FakeFinderById());

        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(1, inner.StartCount);
        Assert.True(supervisor.IsRunning);
    }

    [Fact]
    public async Task Start_IsIdempotent_WhileRunning()
    {
        var inner = new FakeProxyInstance();
        var supervisor = Make(new ForwardProxyConfig(), inner, new FakePortProbe(), new FakeTcpTable(), new FakeFinderById());

        await supervisor.StartAsync(CancellationToken.None);
        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(1, inner.StartCount);
    }

    [Fact]
    public async Task Start_SkipsBind_WhenPortAlreadyHeld()
    {
        // port is busy (GUI in the tray, detached worker or a foreign process), so the second instance does not bind
        var inner = new FakeProxyInstance();
        var ports = new FakePortProbe { Listening = true };
        var tcp = new FakeTcpTable();
        tcp.Listeners[18082] = [Entry(7, "PrivateNetRouting.App")];
        var supervisor = Make(new ForwardProxyConfig(), inner, ports, tcp, new FakeFinderById());

        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(0, inner.StartCount);
        Assert.True(supervisor.IsRunning);
    }

    [Fact]
    public async Task Start_Detached_SpawnsWorkerWithMarkerAndWaitsForListener()
    {
        var inner = new FakeProxyInstance();
        var ports = new FakePortProbe();
        var spawns = new List<(string CommandLine, string? Stdout, string Stderr)>();
        var supervisor = Make(
            new ForwardProxyConfig { Detached = true },
            inner,
            ports,
            new FakeTcpTable(),
            new FakeFinderById(),
            spawn: (commandLine, stdoutPath, stderrPath) =>
            {
                spawns.Add((commandLine, stdoutPath, stderrPath));
                ports.Listening = true; // the worker "came up"
                return 4242;
            },
            exePath: @"C:\app with space\pnr.exe");

        await supervisor.StartAsync(CancellationToken.None);

        var spawn = Assert.Single(spawns);
        Assert.Equal(@"""C:\app with space\pnr.exe"" --proxy-worker", spawn.CommandLine);
        Assert.Null(spawn.Stdout);
        Assert.EndsWith("forward-proxy.err.log", spawn.Stderr);
        Assert.Equal(0, inner.StartCount);
        Assert.True(supervisor.IsRunning);
    }

    [Fact]
    public async Task Start_Detached_DoesNotSpawn_WhenPortAlreadyHeld()
    {
        var inner = new FakeProxyInstance();
        var ports = new FakePortProbe { Listening = true };
        var spawns = new List<(string CommandLine, string? Stdout, string Stderr)>();
        var supervisor = Make(
            new ForwardProxyConfig { Detached = true },
            inner,
            ports,
            new FakeTcpTable(),
            new FakeFinderById(),
            spawn: (commandLine, stdoutPath, stderrPath) =>
            {
                spawns.Add((commandLine, stdoutPath, stderrPath));
                return 4242;
            });

        await supervisor.StartAsync(CancellationToken.None);

        Assert.Empty(spawns);
        Assert.Equal(0, inner.StartCount);
    }

    [Fact]
    public async Task Start_Detached_Fails_WhenWorkerNeverListens()
    {
        var supervisor = Make(
            new ForwardProxyConfig { Detached = true, WorkerStartWaitSeconds = 0 },
            new FakeProxyInstance(),
            new FakePortProbe(),
            new FakeTcpTable(),
            new FakeFinderById());

        var error = await Assert.ThrowsAsync<IOException>(() => supervisor.StartAsync(CancellationToken.None));
        Assert.Contains("did not start listening", error.Message);
    }

    [Fact]
    public async Task Detach_StopsInner_AndSpawnsWorker()
    {
        // the tray "Exit" with the chain kept: the in-process listener must survive the process
        var inner = new FakeProxyInstance();
        var ports = new FakePortProbe();
        var spawns = new List<(string CommandLine, string? Stdout, string Stderr)>();
        var supervisor = Make(
            new ForwardProxyConfig(),
            inner,
            ports,
            new FakeTcpTable(),
            new FakeFinderById(),
            spawn: (commandLine, stdoutPath, stderrPath) =>
            {
                spawns.Add((commandLine, stdoutPath, stderrPath));
                ports.Listening = true; // the worker "came up"
                return 4242;
            });
        await supervisor.StartAsync(CancellationToken.None); // in-process

        await supervisor.DetachAsync(CancellationToken.None);

        var spawn = Assert.Single(spawns);
        Assert.EndsWith(" --proxy-worker", spawn.CommandLine);
        Assert.Equal(1, inner.StopCount);
        Assert.True(supervisor.IsRunning);
    }

    [Fact]
    public async Task Detach_LeavesRunningWorker_AsIs()
    {
        // the listener is already held by our detached worker — nothing to hand over
        var inner = new FakeProxyInstance();
        var ports = new FakePortProbe { Listening = true };
        var tcp = new FakeTcpTable();
        tcp.Listeners[18082] = [Entry(42, "pnr")];
        var processes = new FakeFinderById { CommandLineById = { { 42, @"""C:\app\pnr.exe"" --proxy-worker" } } };
        var spawns = new List<(string CommandLine, string? Stdout, string Stderr)>();
        var supervisor = Make(
            new ForwardProxyConfig { Listen = "127.0.0.1:18082" }, // pinned: the fake table has this port
            inner,
            ports,
            tcp,
            processes,
            spawn: (commandLine, stdoutPath, stderrPath) =>
            {
                spawns.Add((commandLine, stdoutPath, stderrPath));
                return 1;
            });

        await supervisor.DetachAsync(CancellationToken.None);

        Assert.Empty(spawns);
        Assert.Equal(0, inner.StopCount);
    }

    [Fact]
    public async Task Detach_NoListener_IsNoop()
    {
        var inner = new FakeProxyInstance();
        var spawns = new List<(string CommandLine, string? Stdout, string Stderr)>();
        var supervisor = Make(
            new ForwardProxyConfig(),
            inner,
            new FakePortProbe(),
            new FakeTcpTable(),
            new FakeFinderById(),
            spawn: (commandLine, stdoutPath, stderrPath) =>
            {
                spawns.Add((commandLine, stdoutPath, stderrPath));
                return 1;
            });

        await supervisor.DetachAsync(CancellationToken.None);

        Assert.Empty(spawns);
        Assert.Equal(0, inner.StopCount);
    }

    [Fact]
    public async Task Detach_Fails_WhenWorkerNeverListens()
    {
        // the inner instance is already stopped at that point: the exit continues without a proxy
        var inner = new FakeProxyInstance();
        var supervisor = Make(
            new ForwardProxyConfig { WorkerStartWaitSeconds = 0 },
            inner,
            new FakePortProbe(),
            new FakeTcpTable(),
            new FakeFinderById());
        await supervisor.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => supervisor.DetachAsync(CancellationToken.None));

        Assert.Equal(1, inner.StopCount);
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public async Task Stop_StopsInnerInstance_WhenInProcess()
    {
        var inner = new FakeProxyInstance();
        var supervisor = Make(new ForwardProxyConfig(), inner, new FakePortProbe(), new FakeTcpTable(), new FakeFinderById());
        await supervisor.StartAsync(CancellationToken.None);

        await supervisor.StopAsync();

        Assert.Equal(1, inner.StopCount);
    }

    [Fact]
    public async Task Stop_KillsIdentifiedWorker_AndWaitsForPortRelease()
    {
        var ports = new FakePortProbe { Listening = true };
        var tcp = new FakeTcpTable();
        // both listeners (localhost + WSL gateway) belong to one owner
        tcp.Listeners[18082] = [Entry(42, "pnr"), Entry(42, "pnr", WslGateway)];
        var processes = new FakeFinderById { CommandLineById = { { 42, @"""C:\app\pnr.exe"" --proxy-worker" } } };
        var kills = new List<int>();
        var supervisor = Make(
            new ForwardProxyConfig { Listen = "127.0.0.1:18082" }, // pinned: the fake table has this port
            new FakeProxyInstance(),
            ports,
            tcp,
            processes,
            kill: pid =>
            {
                kills.Add(pid);
                ports.Listening = false;
            });

        await supervisor.StopAsync();

        Assert.Equal([42], kills);
    }

    /// <summary>
    /// The idle stopper is a separate process and the GUI hosts the proxy in-process: it asks the
    /// owner through the stop channel instead of leaving a half-stopped chain behind.
    /// </summary>
    [Fact]
    public async Task Stop_AsksAnInProcessOwnerToStopThroughTheChannel()
    {
        var ports = new FakePortProbe { Listening = true };
        var tcp = new FakeTcpTable();
        tcp.Listeners[18082] = [Entry(7, "PrivateNetRouting.App")];
        var processes = new FakeFinderById { CommandLineById = { { 7, @"""C:\app\PrivateNetRouting.App.exe""" } } };
        var channel = new FakeStopChannel { OnRequest = () => ports.Listening = false };
        var kills = new List<int>();
        var supervisor = Make(
            new ForwardProxyConfig { Listen = "127.0.0.1:18082" },
            new FakeProxyInstance(),
            ports,
            tcp,
            processes,
            kill: kills.Add,
            channel: channel);

        await supervisor.StopAsync();

        Assert.Equal([18082], channel.Requested);
        Assert.Empty(kills); // the owner is asked, never killed
        Assert.False(ports.Listening);
    }

    [Fact]
    public async Task Stop_WithNoOwnerListeningOnTheChannelStillLeavesAForeignOwnerAlone()
    {
        var ports = new FakePortProbe { Listening = true };
        var tcp = new FakeTcpTable();
        tcp.Listeners[18082] = [Entry(7, "somebody-else")];
        var channel = new FakeStopChannel { Answers = false };
        var supervisor = Make(
            new ForwardProxyConfig { Listen = "127.0.0.1:18082", WorkerStopWaitSeconds = 0 },
            new FakeProxyInstance(),
            ports,
            tcp,
            new FakeFinderById(),
            channel: channel);

        await supervisor.StopAsync();

        Assert.Equal([18082], channel.Requested);
        Assert.True(ports.Listening);
    }

    /// <summary>An in-process proxy listens for stop requests while it runs and stops itself on one.</summary>
    [Fact]
    public async Task InProcessProxyStopsItselfOnAStopRequest()
    {
        var inner = new FakeProxyInstance();
        var channel = new FakeStopChannel();
        var supervisor = Make(new ForwardProxyConfig(), inner, new FakePortProbe(), new FakeTcpTable(), new FakeFinderById(), channel: channel);
        await supervisor.StartAsync(CancellationToken.None);
        Assert.NotNull(channel.Handler);

        await channel.Handler!();

        Assert.Equal(1, inner.StopCount);
        Assert.True(channel.ListenerDisposed); // one request is enough, the listening ends with the listener

        // a normal stop ends the listening too
        var inner2 = new FakeProxyInstance();
        var channel2 = new FakeStopChannel();
        var second = Make(new ForwardProxyConfig(), inner2, new FakePortProbe(), new FakeTcpTable(), new FakeFinderById(), channel: channel2);
        await second.StartAsync(CancellationToken.None);
        await second.StopAsync();
        Assert.True(channel2.ListenerDisposed);
    }

    [Fact]
    public async Task NamedEventChannelDeliversARequestToTheListeningOwner()
    {
        const int port = 59871; // not a real listener: the channel is keyed by number only
        var channel = new NamedEventProxyStopChannel();
        Assert.False(channel.Request(port)); // nobody listens yet
        var requested = new TaskCompletionSource();
        using (channel.Listen(port, () =>
        {
            requested.TrySetResult();
            return Task.CompletedTask;
        }))
        {
            Assert.True(channel.Request(port));
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(channel.Request(port)); // the listening ended with the listener
    }

    private sealed class FakeStopChannel : IProxyStopChannel
    {
        public List<int> Requested { get; } = [];

        public bool Answers { get; init; } = true;

        public Action? OnRequest { get; init; }

        public Func<Task>? Handler { get; private set; }

        public bool ListenerDisposed { get; private set; }

        public IDisposable Listen(int port, Func<Task> onStopRequested)
        {
            Handler = onStopRequested;
            return new Disposer(this);
        }

        public bool Request(int port)
        {
            Requested.Add(port);
            OnRequest?.Invoke();
            return Answers;
        }

        private sealed class Disposer(FakeStopChannel owner) : IDisposable
        {
            public void Dispose() => owner.ListenerDisposed = true;
        }
    }

    [Fact]
    public async Task Stop_LeavesForeignOwner_Alone()
    {
        // the GUI holds the port (no marker): chain shutdown does not kill it, only logs a warning
        var ports = new FakePortProbe { Listening = true };
        var tcp = new FakeTcpTable();
        tcp.Listeners[18082] = [Entry(7, "PrivateNetRouting.App")];
        var processes = new FakeFinderById { CommandLineById = { { 7, @"""C:\app\PrivateNetRouting.App.exe""" } } };
        var kills = new List<int>();
        var supervisor = Make(
            new ForwardProxyConfig(),
            new FakeProxyInstance(),
            ports,
            tcp,
            processes,
            kill: kills.Add);

        await supervisor.StopAsync();

        Assert.Empty(kills);
        Assert.True(ports.Listening);
    }

    [Fact]
    public async Task Stop_NoOwners_IsNoop()
    {
        var inner = new FakeProxyInstance();
        var kills = new List<int>();
        var supervisor = Make(
            new ForwardProxyConfig(),
            inner,
            new FakePortProbe(),
            new FakeTcpTable(),
            new FakeFinderById(),
            kill: kills.Add);

        await supervisor.StopAsync();

        Assert.Empty(kills);
        Assert.Equal(0, inner.StopCount);
    }

    [Fact]
    public async Task Stop_WarnsWhenPortStaysBusy_AfterKill()
    {
        var ports = new FakePortProbe { Listening = true };
        var tcp = new FakeTcpTable();
        tcp.Listeners[18082] = [Entry(42, "pnr")];
        var processes = new FakeFinderById { CommandLineById = { { 42, @"""C:\app\pnr.exe"" --proxy-worker" } } };
        var supervisor = Make(
            new ForwardProxyConfig { WorkerStopWaitSeconds = 0 },
            new FakeProxyInstance(),
            ports,
            tcp,
            processes,
            kill: _ => { }); // the port is not released

        await supervisor.StopAsync(); // does not throw, only warns to the log

        Assert.True(ports.Listening);
    }

    [Fact]
    public void ListenerEndpoints_ComeFromTcpTable_WhenInnerIsStopped()
    {
        var tcp = new FakeTcpTable();
        tcp.Listeners[18082] = [Entry(42, "pnr"), Entry(42, "pnr", WslGateway)];
        var supervisor = Make(
            new ForwardProxyConfig { Listen = "127.0.0.1:18082" }, // pinned: the fake table has this port
            new FakeProxyInstance(),
            new FakePortProbe { Listening = true },
            tcp,
            new FakeFinderById());

        var endpoints = supervisor.ListenerEndpoints.OrderBy(endpoint => endpoint).ToList();

        Assert.Equal(["10.0.2.2:18082", "127.0.0.1:18082"], endpoints);
    }

    [Fact]
    public void ListenerEndpoints_ComeFromInner_WhileRunning()
    {
        var inner = new FakeProxyInstance { IsRunning = true, ListenerEndpoints = ["127.0.0.1:18082"] };
        var supervisor = Make(new ForwardProxyConfig(), inner, new FakePortProbe(), new FakeTcpTable(), new FakeFinderById());

        Assert.Equal(["127.0.0.1:18082"], supervisor.ListenerEndpoints);
    }
}
