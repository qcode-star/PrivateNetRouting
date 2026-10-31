using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Updater;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Cli;

/// <summary>
/// Headless mode: proxy — runs the forward proxy in the console
/// (or brings up a detached worker), chain — chain control (status/up/down), check —
/// CONNECT roundtrip, status — diagnostics summary, logs —
/// viewing/rotation of logs, config — path/initialization. Elevated commands (--elevated-*)
/// are executed by the internal helper and it exits; --proxy-worker and --watchdog-worker — the bodies
/// of the detached proxy and idle-stop workers.
/// </summary>
internal static class Program
{
    /// <summary>Cancelled by the first Ctrl+C; the long-running commands take their token from here.</summary>
    private static readonly CancellationTokenSource Interrupt = new();

    private static async Task<int> Main(string[] args)
    {
        var first = args.FirstOrDefault() ?? "help";
        if (first == ForwardProxySupervisor.WorkerMarker)
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            return await ProxyWorker.RunAsync(stop.Token, args);
        }

        // the detached idle-stop worker: a chain raise from a CLI process spawns this exe with
        // the marker (WatchdogSupervisor.WorkerHostNames), so the CLI hosts the worker too
        if (first == WatchdogSupervisor.WorkerMarker)
        {
            return await Task.Run(() => WatchdogWorker.RunAsync(CancellationToken.None));
        }

        if (ElevatedHelperArguments.IsHelperCommand(first))
        {
            var resultFile = ElevatedHelperArguments.ParseOption(args, ElevatedHelperArguments.ResultFileOption);
            if (resultFile is null)
            {
                Console.Error.WriteLine($"error: {first} is an internal helper command and requires {ElevatedHelperArguments.ResultFileOption}");
                return 2;
            }

            // the config of the user who asked: this process may run as another account (over-the-shoulder UAC)
            var configPath = ElevatedHelperArguments.ParseOption(args, ElevatedHelperArguments.ConfigOption);
            var profileEnvironment = ElevatedHelperArguments.ParseOptions(args, ElevatedHelperArguments.EnvOption);
            return GuardElevatedCommands.IsCommand(first)
                ? GuardElevatedCommands.Execute(first, resultFile, configPath, profileEnvironment)
                : ElevatedChainCommands.Execute(first, resultFile, configPath, profileEnvironment);
        }

        // the text carries em dashes, arrows and ellipses: an OEM code page would mangle them
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (IOException)
        {
            // no console attached: the redirected streams keep their default encoding
        }

        // one Ctrl+C handler for every command: the first press cancels the running operation through
        // Interrupt, a second one (the operation did not stop) falls through to the default hard kill
        Console.CancelKeyPress += (_, e) =>
        {
            if (!Interrupt.IsCancellationRequested)
            {
                e.Cancel = true;
                Interrupt.Cancel();
            }
        };

        try
        {
            return first switch
            {
                "--version" or "version" => Version(),
                "help" or "--help" or "-h" or "-?" or "/?" => Help(),
                "proxy" => await RunProxyAsync(args.Skip(1).ToList()),
                "chain" => await RunChainAsync(args.Skip(1).ToList()),
                "check" => await RunCheckAsync(args.Skip(1).ToList()),
                "status" => await RunStatusAsync(args.Skip(1).ToList()),
                "logs" => RunLogsCommand(args.Skip(1).ToList()),
                "launch" => await RunLaunchAsync(args.Skip(1).ToList()),
                "browser" => await RunBrowserAsync(args.Skip(1).ToList()),
                "codex" => await RunCodexAsync(args.Skip(1).ToList()),
                "update" => await RunUpdateAsync(args.Skip(1).ToList()),
                "watchdog" => await RunWatchdogAsync(args.Skip(1).ToList()),
                "guard" => await RunGuardAsync(args.Skip(1).ToList()),
                "config" => ConfigCommand(args.Skip(1).ToList()),
                _ => UnknownCommand(first),
            };
        }
        catch (OperationCanceledException) when (Interrupt.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Without flags — expert mode: the proxy runs in this process (Ctrl+C to stop),
    /// regardless of forwardProxy.detached. With --detached — bring up a detached worker
    /// (it survives the command exit) and return.
    /// </summary>
    private static async Task<int> RunProxyAsync(List<string> rest)
    {
        var detached = false;
        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--detached":
                    detached = true;
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {argument} (--detached)");
                    return 1;
            }
        }

        var config = AppConfigStore.LoadOrCreate();
        if (AppProxyEndpoint.IsSkipped(config))
        {
            Console.WriteLine(config.Chain.EffectiveMode == ChainMode.Http
                ? "the local forward proxy does not run in the http mode — " +
                  $"the applications point at {config.Chain.Http.Upstream}"
                : "the local forward proxy is skipped in the socks5 mode (forwardProxy.enabled=false) — " +
                  $"the applications point at {config.Chain.Socks5.Upstream}");
            return 0;
        }

        if (!config.ForwardProxy.Enabled)
        {
            Console.WriteLine("forward proxy is disabled in config (forwardProxy.enabled=false)");
            return 0;
        }

        if (detached)
        {
            config.ForwardProxy.Detached = true; // the flag overrides the config — for this run only
            await using var runtime = ProxyRuntime.FromConfig(config);
            await runtime.ProxyControl.StartAsync(CancellationToken.None);
            Console.WriteLine($"forward proxy: listening on {string.Join(", ", runtime.ProxyControl.ListenerEndpoints)} (detached worker)");
            Console.WriteLine($"log: {runtime.Log.FilePath}");
            return 0;
        }

        if (config.ForwardProxy.Detached)
        {
            Console.WriteLine("note: forwardProxy.detached=true — the detached worker may hold the port; stop it first (pnr chain down)");
        }

        config.ForwardProxy.Detached = false; // the foreground mode overrides the config — for this run only
        await using var consoleRuntime = ProxyRuntime.FromConfig(config);
        try
        {
            // through the supervisor, not the raw service: only that start registers the proxy stop
            // channel, so the detached idle stopper (and `pnr chain down`) can ask this process to
            // stop its own listener instead of leaving a half-stopped chain behind
            await consoleRuntime.ProxyControl.StartAsync(Interrupt.Token);
            if (consoleRuntime.ProxyService is not { } service || !service.IsRunning)
            {
                Console.Error.WriteLine("forward proxy: the port is held by another owner — nothing to run in this process (see the log)");
                return 1;
            }

            Console.WriteLine($"forward proxy: listening on {string.Join(", ", consoleRuntime.ProxyControl.ListenerEndpoints)}");
            Console.WriteLine($"log: {consoleRuntime.Log.FilePath}");
            Console.WriteLine("Ctrl+C to stop");
            while (!Interrupt.Token.IsCancellationRequested && service.IsRunning)
            {
                // a channel-requested stop (the idle stopper) releases the listener without a
                // Ctrl+C — this loop is what ends the command then
                await Task.Delay(500, Interrupt.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is how this command is meant to end
        }

        Console.WriteLine("forward proxy: stopped");
        return 0;
    }

    private static async Task<int> RunChainAsync(List<string> rest)
    {
        var action = rest.FirstOrDefault() ?? "status";
        if (RejectExtraArguments("pnr chain status|up|down", rest, 1))
        {
            return 1;
        }

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        var progress = new SyncProgress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));

        switch (action)
        {
            case "status":
                var state = await runtime.Chain.GetStateAsync(Interrupt.Token);
                var skipProxy = AppProxyEndpoint.IsSkipped(config);
                if (config.Chain.EffectiveMode != ChainMode.Tailscale)
                {
                    var external = config.Chain.EffectiveMode == ChainMode.Http
                        ? config.Chain.Http.Upstream
                        : string.IsNullOrEmpty(config.Chain.Socks5.User)
                            ? config.Chain.Socks5.Upstream
                            : $"{config.Chain.Socks5.User}@{config.Chain.Socks5.Upstream}";
                    Console.WriteLine($"upstream:  {external}, {(state.UpstreamReachable is true ? "reachable" : state.UpstreamReachable is false ? "no route" : "not checked")}");
                }
                else
                {
                    Console.WriteLine($"daemon:    listener {(state.DaemonHttpListening ? "up" : "down")}, process {(state.DaemonProcessAlive ? "alive" : "not found")}");
                    Console.WriteLine($"keeper:    {(state.KeeperRunning ? "running" : "not found")}");
                    Console.WriteLine($"node:      BackendState={state.BackendState ?? "no response"}");
                }

                Console.WriteLine(skipProxy
                    ? $"proxy:     skipped (the applications point at {AppProxyEndpoint.Address(config)}), health {(state.ProxyHealthy ? "alive" : "failed")}"
                    : $"proxy:     {(state.ProxyRunning == true ? $"running ({string.Join(", ", runtime.ProxyControl.ListenerEndpoints)})" : "stopped")}, health {(state.ProxyHealthy ? "alive" : "failed")}");
                Console.WriteLine(state.IsFullyUp ? "chain: fully up" : state.IsStopped ? "chain: stopped" : "chain: partial");
                return 0;

            case "up":
                await runtime.Chain.EnsureRunningAsync(progress, Interrupt.Token);
                Console.WriteLine("chain is up");
                await runtime.WatchdogControl.LastAutoStart; // the re-arm spawn must survive this process
                return await HandOverProxyAsync(runtime) ? 0 : 1;

            case "down":
                await runtime.Chain.StopAsync(progress, Interrupt.Token);
                Console.WriteLine("chain is stopped");
                return 0;

            default:
                Console.Error.WriteLine($"unknown action: {action} (status | up | down)");
                return 1;
        }
    }

    /// <summary>
    /// For the commands that leave a client behind (a launched app, a browser, a raised chain):
    /// the in-process forward proxy would die with this process and the client would be left
    /// pointed at a dead port, so the listener goes to a detached worker first — the same
    /// handover as the tray "Exit". A no-op when a worker, the GUI or nobody holds the port, and
    /// in the skipped shapes. False — the worker did not come up; the caller exits non-zero.
    /// </summary>
    private static Task<bool> HandOverProxyAsync(ProxyRuntime runtime) => HandOverProxyAsync(runtime, null);

    /// <summary>The handover failed after a chain was raised by a command that leaves no client of its own.</summary>
    private const string ChainLeftUpMessage =
        "the chain was raised, but nothing will listen on the proxy address once this command exits";

    /// <summary>The re-arm spawn on a failure path: its own failure must not replace the one being reported.</summary>
    private static async Task AwaitAutoStartQuietlyAsync(ProxyRuntime runtime)
    {
        try
        {
            await runtime.WatchdogControl.LastAutoStart;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"the idle stopper could not be re-armed: {ex.Message}");
        }
    }

    private static async Task<bool> HandOverProxyAsync(ProxyRuntime runtime, string? consequence)
    {
        try
        {
            await runtime.HandOverProxyAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // a worker that cannot be spawned (Win32Exception) is the same failed handover: on an
            // error path it must not replace the exception being reported
            Console.Error.WriteLine($"the forward proxy could not be handed over to a background worker: {ex.Message}");
            Console.Error.WriteLine(consequence
                ?? "the client was started, but nothing will listen on the proxy address once this command exits");
            return false;
        }
    }

    private static async Task<int> RunCheckAsync(List<string> rest)
    {
        if (RejectExtraArguments("pnr check", rest, 0))
        {
            return 1;
        }

        var config = AppConfigStore.LoadOrCreate();
        var fp = config.ForwardProxy;
        var endpoint = AppProxyEndpoint.EndPoint(config);
        Console.WriteLine($"CONNECT-roundtrip {endpoint} -> {fp.HealthCheckHost}:{fp.HealthCheckPort}...");
        await using var runtime = ProxyRuntime.FromConfig(config);
        var alive = await runtime.CheckHealthAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine(alive ? "chain is alive" : "chain is not responding");
        return alive ? 0 : 2;
    }

    /// <summary>
    /// State summary, read-only: chain processes and listener
    /// owners (PID), WSL bind, proxy counters, activity marker age, detailed
    /// CONNECT roundtrip, policy and ban, watchdog liveness. A fully stopped chain —
    /// a short message: this is normal while Claude and Codex are not needed.
    /// </summary>
    private static async Task<int> RunStatusAsync(List<string> rest)
    {
        if (RejectExtraArguments("pnr status", rest, 0))
        {
            return 1;
        }

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        var summary = await runtime.Diagnostics.GetSummaryAsync(Interrupt.Token);
        if (summary.ChainStopped)
        {
            Console.WriteLine($"chain ({AppProxyEndpoint.Address(config)}) is stopped — normal while Claude and Codex are not needed.");
            Console.WriteLine("Raise it: the Claude shortcut, pnr launch, or pnr chain up.");
            return 0;
        }

        foreach (var section in summary.Sections)
        {
            Console.WriteLine($"{section.Key}:");
            foreach (var line in section.Lines)
            {
                Console.WriteLine($"  {line.Name,-34} {line.Value}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Viewing and rotation of logs in the logging.directory directory (read-only plus file
    /// shifting; nothing is started). The config is read with LoadOrCreate, so a missing one is created
    /// from the defaults. A file held by another process (the application's live log) is reported as
    /// locked.
    /// </summary>
    private static int RunLogsCommand(List<string> rest)
    {
        var action = rest.FirstOrDefault() ?? "list";
        rest = rest.Skip(1).ToList();
        var config = AppConfigStore.LoadOrCreate();
        var store = new LogStore(config.Logging.Directory);

        switch (action)
        {
            case "list":
                var files = store.List();
                if (files.Count == 0)
                {
                    Console.WriteLine($"no log files in {store.Directory}");
                    return 0;
                }

                foreach (var file in files)
                {
                    Console.WriteLine($"{file.Name,-24} {ByteSize.Format(file.Bytes),10}  {file.LastWriteTime:yyyy-MM-dd HH:mm}");
                }

                return 0;

            case "show":
                return ShowLogTail(store, rest);

            case "rotate":
                return RotateLogs(store, config, rest);

            default:
                Console.Error.WriteLine($"unknown action: {action} (list | show <name> [--tail N] | rotate [<name>...])");
                return 1;
        }
    }

    private static int ShowLogTail(LogStore store, List<string> rest)
    {
        var name = rest.FirstOrDefault();
        var tail = 50;
        for (var i = 1; i < rest.Count; i++)
        {
            if (rest[i] == "--tail" && i + 1 < rest.Count && int.TryParse(rest[i + 1], out var lines))
            {
                tail = lines;
                i++;
            }
            else
            {
                Console.Error.WriteLine($"unknown option: {rest[i]} (--tail N)");
                return 1;
            }
        }

        if (name is null)
        {
            Console.Error.WriteLine("usage: pnr logs show <name> [--tail N]");
            return 1;
        }

        var log = store.ReadTail(name, tail);
        if (log is null)
        {
            Console.Error.WriteLine($"no log file named {name} in {store.Directory} (see pnr logs list)");
            return 1;
        }

        foreach (var line in log.Lines)
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static int RotateLogs(LogStore store, AppConfig config, List<string> names)
    {
        // without a name — all current logs (*.log); rotated copies (.N) are left alone
        var targets = names.Count > 0
            ? names
            : store.List().Where(f => f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Select(f => f.Name).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine($"no log files in {store.Directory}");
            return 0;
        }

        var failed = false;
        foreach (var name in targets)
        {
            var result = store.Rotate(name, config.Logging.Keep);
            Console.WriteLine($"{name,-24} {result.Detail}");
            failed |= !result.Ok;
        }

        if (failed)
        {
            Console.WriteLine("locked files are held by the running app — rotate them from its diagnostics window");
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// The restart offer of the CLI: an application started outside the launcher keeps its own
    /// flags (it is single-instance — a second start only activates its window). true — close it
    /// and start again through the proxy, false — launch without a restart, null — the owner
    /// declined, the launch is cancelled. --restart answers "yes" without asking; a redirected
    /// stdin never asks and continues without a restart, so scripts keep working.
    /// </summary>
    private static bool? ConfirmRestart(RunningAppInfo running, string appName, bool forceRestart)
    {
        if (running.State != RunningAppState.WithoutProxy)
        {
            return false;
        }

        Console.WriteLine(running.ForeignProxyUrl is null
            ? $"{appName} is already running ({running.ProcessCount} processes) without our proxy — a new start would only activate its window"
            : $"{appName} is already running ({running.ProcessCount} processes) with a different proxy [{running.ForeignProxyUrl}] — a new start would only activate its window");

        if (forceRestart)
        {
            Console.WriteLine($"{appName} is closed and started again through the proxy (--restart)");
            return true;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"not restarting {appName}: no interactive input (pass --restart to close it and start again through the proxy)");
            return false;
        }

        Console.Write($"close {appName} and start it again through the proxy? [y/N]: ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" ? true : null;
    }

    private static async Task<int> RunLaunchAsync(List<string> rest)
    {
        var checkOnly = false;
        var forceRestart = false;
        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--check-only":
                    checkOnly = true;
                    break;
                case "--no-splash":
                    break; // the bring-up is drawn by the GUI window; accepted for shortcut compatibility
                case "--restart":
                    forceRestart = true;
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {argument} (--check-only | --no-splash | --restart)");
                    return 1;
            }
        }

        var config = AppConfigStore.LoadOrCreate();
        if (!config.Claude.Enabled)
        {
            Console.Error.WriteLine("Claude is disabled in config (claude.enabled=false)");
            return 1;
        }

        await using var runtime = ProxyRuntime.FromConfig(config);
        var progress = new SyncProgress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));

        var restart = false;
        if (!checkOnly)
        {
            var answer = ConfirmRestart(runtime.Launcher.InspectRunning(), "Claude", forceRestart);
            if (answer is null)
            {
                runtime.Launcher.NoteRestartDeclined();
                Console.WriteLine("launch cancelled: the running copy works without the proxy");
                return 0;
            }

            restart = answer.Value;
        }

        var result = await runtime.Launcher.LaunchAsync(new LauncherOptions(checkOnly, restart), progress, Interrupt.Token);
        await runtime.WatchdogControl.LastAutoStart; // the re-arm spawn must survive this process
        var handedOver = await HandOverProxyAsync(runtime);
        if (result.Outcome == LaunchOutcome.CheckOnly)
        {
            Console.WriteLine(result.ChainRaised ? "OK: chain is up" : "OK: chain healthy");
            return handedOver ? 0 : 1;
        }

        Console.WriteLine(result.RestartedExisting
            ? $"claude restarted through the proxy (proxy {runtime.Launcher.ProxyUrl}, chain raised: {result.ChainRaised})"
            : $"claude launched (proxy {runtime.Launcher.ProxyUrl}, chain raised: {result.ChainRaised})");
        return handedOver ? 0 : 1;
    }

    private static async Task<int> RunBrowserAsync(List<string> rest)
    {
        var options = BrowserLauncherOptions.Default;
        string? url = null;
        var assumeYes = false;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--check-only":
                    options = options with { CheckOnly = true };
                    break;
                case "--private":
                    options = options with { Private = true };
                    break;
                case "--yes":
                    assumeYes = true;
                    break;
                case "--url":
                    if (i + 1 >= rest.Count || rest[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("--url requires a value");
                        return 1;
                    }

                    url = rest[++i];
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {rest[i]} (--check-only | --private | --yes | --url <url>)");
                    return 1;
            }
        }

        if (url is not null)
        {
            options = options with { Url = url };
        }

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        if (!runtime.Browser.IsConfigured)
        {
            Console.Error.WriteLine(
                "the browser profile is not created yet — create it in the application (\"?\" → \"Create browser profile…\")");
            return 1;
        }

        if (options.CheckOnly)
        {
            // read-only: the chain is not touched, use `pnr chain up` for that
            var reachable = await runtime.Browser.IsEgressReachableAsync(Interrupt.Token);
            Console.WriteLine(reachable
                ? $"OK: the egress {runtime.Browser.EgressUrl} responds"
                : $"the egress {runtime.Browser.EgressUrl} is not responding");
            return reachable ? 0 : 1;
        }

        if (runtime.Browser.IsOurBrowserRunning())
        {
            Console.WriteLine("the browser is already running with our profile — bringing it up is up to the taskbar");
            return 0;
        }

        // A dead egress: when the egress is ours to raise (ChainOwnsEgress — the forward proxy in
        // the default shape, the sidecar's listener in the tailscale mode) a confirmed answer raises the
        // chain (the launcher does that); over an external endpoint (the upstream of the socks5 or http mode) there is
        // nothing to raise — the question is only whether to start anyway.
        var askedWithoutInput = false;
        Task<bool> ConfirmDeadEgress(CancellationToken ct)
        {
            var egress = runtime.Browser.EgressUrl;
            if (assumeYes)
            {
                return Task.FromResult(true);
            }

            if (Console.IsInputRedirected)
            {
                // the question could never be answered: a piped line would be taken as the answer
                Console.Error.WriteLine(runtime.Browser.ChainOwnsEgress
                    ? $"the browser egress {egress} is not responding; no interactive input — pass --yes to raise the chain and start the browser"
                    : $"the upstream {egress} is not responding; no interactive input — pass --yes to start the browser anyway");
                askedWithoutInput = true;
                return Task.FromResult(false);
            }

            Console.Write(runtime.Browser.ChainOwnsEgress
                ? $"the browser egress {egress} is not responding. Raise the chain and start the browser? [y/N]: "
                : $"the upstream {egress} is not responding. Start the browser anyway? [y/N]: ");
            return Task.FromResult(Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes");
        }

        var progress = new SyncProgress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));
        var result = await runtime.Browser.LaunchAsync(
            options with { ConfirmDeadEgress = ConfirmDeadEgress },
            progress,
            Interrupt.Token);
        await runtime.WatchdogControl.LastAutoStart; // the re-arm spawn must survive this process
        var handedOver = await HandOverProxyAsync(runtime);
        if (result.Outcome == BrowserLaunchOutcome.Cancelled)
        {
            Console.WriteLine("launch cancelled: the egress is not responding");
            return askedWithoutInput ? 1 : 0;
        }

        Console.WriteLine(result.EgressResponded
            ? $"browser launched over the ready egress {runtime.Browser.EgressUrl}"
            : $"browser launched over the egress {runtime.Browser.EgressUrl} that had not answered");
        return handedOver ? 0 : 1;
    }

    private static async Task<int> RunCodexAsync(List<string> rest)
    {
        var action = rest.FirstOrDefault() ?? "help";
        var options = rest.Skip(1).ToList();
        switch (action)
        {
            case "launch":
                return await RunCodexLaunchAsync(options);
            case "update":
                return await RunCodexUpdateAsync(options);
            default:
                // an unknown action is an error: the usage goes to stderr, only an asked-for help to stdout
                var usage = action == "help" ? Console.Out : Console.Error;
                usage.WriteLine("""
                    usage: pnr codex launch [--check-only] [--restart]
                           pnr codex update [--check]

                    launch  start the Codex desktop app with --proxy-server/--proxy-bypass-list
                            and the proxy env (Invoke-CommandInDesktopPackage, codex.* config),
                            bringing the chain up if needed; --restart closes a copy that is
                            already running without our proxy and starts it again
                    update  update the app through the Microsoft Store (versions check first,
                            a running Codex is closed before the Store replaces the package)
                    """);
                return action == "help" ? 0 : 1;
        }
    }

    private static async Task<int> RunCodexLaunchAsync(List<string> rest)
    {
        var checkOnly = false;
        var forceRestart = false;
        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--check-only":
                    checkOnly = true;
                    break;
                case "--restart":
                    forceRestart = true;
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {argument} (--check-only | --restart)");
                    return 1;
            }
        }

        var config = AppConfigStore.LoadOrCreate();
        if (!config.Codex.Enabled)
        {
            Console.Error.WriteLine("Codex is disabled in config (codex.enabled=false)");
            return 1;
        }

        await using var runtime = ProxyRuntime.FromConfig(config);
        var progress = new SyncProgress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));

        var restart = false;
        if (!checkOnly)
        {
            var answer = ConfirmRestart(runtime.CodexLauncher.InspectRunning(), "Codex", forceRestart);
            if (answer is null)
            {
                runtime.CodexLauncher.NoteRestartDeclined();
                Console.WriteLine("codex launch cancelled: the running copy works without the proxy");
                return 0;
            }

            restart = answer.Value;
        }

        var result = await runtime.CodexLauncher.LaunchAsync(new LauncherOptions(checkOnly, restart), progress, Interrupt.Token);
        await runtime.WatchdogControl.LastAutoStart; // the re-arm spawn must survive this process
        var handedOver = await HandOverProxyAsync(runtime);
        if (result.Outcome == CodexLaunchOutcome.CheckOnly)
        {
            Console.WriteLine(result.ChainRaised ? "OK: chain is up" : "OK: chain healthy");
            return handedOver ? 0 : 1;
        }

        Console.WriteLine(result.RestartedExisting
            ? $"codex restarted through the proxy (proxy {runtime.CodexLauncher.ProxyUrl}, chain raised: {result.ChainRaised})"
            : $"codex launched (proxy {runtime.CodexLauncher.ProxyUrl}, chain raised: {result.ChainRaised})");
        return handedOver ? 0 : 1;
    }

    private static async Task<int> RunCodexUpdateAsync(List<string> rest)
    {
        var checkOnly = false;
        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--check":
                    checkOnly = true;
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {argument} (--check)");
                    return 1;
            }
        }

        var config = AppConfigStore.LoadOrCreate();
        if (!config.Codex.Enabled)
        {
            Console.Error.WriteLine("Codex is disabled in config (codex.enabled=false)");
            return 1;
        }

        await using var runtime = ProxyRuntime.FromConfig(config);
        var check = await runtime.CodexUpdater.CheckAsync(Interrupt.Token);
        Console.WriteLine($"installed: {check.Installed.Version} (package {check.Installed.Name})");
        if (check.StoreUnavailable)
        {
            Console.WriteLine("the Store did not answer the update search");
            return 1;
        }

        if (!check.UpdateAvailable)
        {
            Console.WriteLine("up to date — the Store has nothing newer");
            return 0;
        }

        Console.WriteLine(check.OfferedVersion is null
            ? "update available (the Store did not name the version)"
            : $"update available: {check.OfferedVersion}");
        if (checkOnly)
        {
            return 0;
        }

        var progress = new SyncProgress<UpdateProgress>(p =>
            Console.WriteLine($"[{p.Phase}] {p.Message}"));
        var update = await runtime.CodexUpdater.UpdateAsync(progress, Interrupt.Token);
        if (!update.VersionChanged)
        {
            // a Store item that completed without registering a new version is not "updated"
            Console.Error.WriteLine(
                $"codex: {update.Package.Version} — the version did not change after the Store update");
            return 1;
        }

        Console.WriteLine($"codex updated: {check.Installed.Version} -> {update.Package.Version}");
        return 0;
    }

    private static async Task<int> RunWatchdogAsync(List<string> rest)
    {
        if (RejectExtraArguments("pnr watchdog", rest, 0))
        {
            return 1;
        }

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);

        // the stopper exists to stop a live chain: over a down one it would only wait forever
        // (the usage probe sees the running Claude and never reports idle)
        var state = await runtime.Chain.GetStateAsync(Interrupt.Token);
        if (!state.IsFullyUp)
        {
            Console.WriteLine("watchdog: the chain is not up — nothing to stop; raise the chain first (pnr chain up)");
            return 1;
        }

        // one stopper is enough — the same self-check the detached worker applies to itself
        var refusal = WatchdogWorker.RefusalReason(
            runtime.WatchdogControl.FindStopperPids(), Environment.ProcessId, state);
        if (refusal is not null)
        {
            Console.WriteLine($"watchdog: {refusal}");
            return 0;
        }

        Console.WriteLine($"watchdog: checking Claude and Codex usage every {config.Watchdog.CheckSeconds}s; Ctrl+C to stop");
        if (config.Wsl.Distros.Count == 0)
        {
            Console.WriteLine("watchdog: no distros configured (wsl.distros), the WSL step will be skipped on idle");
        }

        var progress = new SyncProgress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));
        try
        {
            await runtime.Watchdog.RunAsync(progress, Interrupt.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("watchdog: cancelled");
            return 0;
        }

        Console.WriteLine("watchdog: idle stop complete");
        return 0;
    }

    /// <summary>
    /// Manual update of Claude Desktop (MSIX) through the proxy chain. Versions are checked
    /// before bringing up the chain — "nothing to install"
    /// brings up nothing and asks for no UAC.
    /// </summary>
    private static async Task<int> RunUpdateAsync(List<string> rest)
    {
        var options = UpdateOptions.Default;
        var checkOnly = false;
        string? installFrom = null;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--check":
                    checkOnly = true;
                    break;
                case "--download-only":
                    options = options with { DownloadOnly = true };
                    break;
                case "--force":
                    options = options with { Force = true };
                    break;
                case "--keep-file":
                    options = options with { KeepFile = true };
                    break;
                case "--keep-running":
                    options = options with { KeepRunning = true };
                    break;
                case "--url":
                    if (i + 1 >= rest.Count || rest[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("--url requires a value");
                        return 1;
                    }

                    options = options with { ManualUrl = rest[++i] };
                    break;
                case "--install-from":
                    if (i + 1 >= rest.Count || rest[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("--install-from requires a value");
                        return 1;
                    }

                    installFrom = rest[++i];
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {rest[i]} (--check | --download-only | --force | --keep-file | --keep-running | --url <url> | --install-from <path>)");
                    return 1;
            }
        }

        if (checkOnly && installFrom is not null)
        {
            // --check is read-only: it must never turn into an installation
            Console.Error.WriteLine("--check cannot be combined with --install-from");
            return 1;
        }

        if (options.DownloadOnly && installFrom is not null)
        {
            // the two flags contradict each other: one says "do not install", the other names a
            // package to install — installing would close Claude against an explicit instruction
            Console.Error.WriteLine("--download-only cannot be combined with --install-from");
            return 1;
        }

        var config = AppConfigStore.LoadOrCreate();
        if (!config.Claude.Enabled)
        {
            Console.Error.WriteLine("Claude is disabled in config (claude.enabled=false)");
            return 1;
        }

        await using var runtime = ProxyRuntime.FromConfig(config);
        var progress = new SyncProgress<UpdateProgress>(p =>
            Console.WriteLine(p.TotalBytes > 0 && p.Phase == UpdatePhase.Download
                ? $"  download {ByteSize.Format(p.DoneBytes)} of {ByteSize.Format(p.TotalBytes)}"
                : $"[{p.Phase.ToString().ToLowerInvariant()}] {p.Message}"));
        var chainProgress = new SyncProgress<ChainStep>(step =>
            Console.WriteLine($"  [{step.Status,-7}] {step.Component}: {step.Detail}"));

        if (installFrom is not null)
        {
            var install = await runtime.Updater.InstallAsync(installFrom, options, progress, Interrupt.Token);
            if (install.AlreadyInstalled)
            {
                Console.WriteLine($"up to date: the package is the installed version {install.Package.Version} — nothing to install (pass --force to reinstall it)");
                return 0;
            }

            if (!install.VersionChanged)
            {
                // --force accepts the reinstall it asked for, never an update that did not land
                (install.Reinstall ? Console.Out : Console.Error).WriteLine(install.Reinstall
                    ? $"reinstalled: {install.Package.Version} — the same version, as asked by --force"
                    : $"not installed: {install.Package.Version} — the installed version did not reach the package's after the install");
                return install.Reinstall ? 0 : 1;
            }

            Console.WriteLine($"installed: {install.Package.Version}");
            return 0;
        }

        if (checkOnly)
        {
            var check = await runtime.Updater.CheckAsync(options, Interrupt.Token);
            if (check.Installed is null)
            {
                Console.WriteLine("Claude Desktop is not installed as an MSIX package");
                return 1;
            }

            Console.WriteLine($"installed: {check.Installed.Version}");
            Console.WriteLine(check.PackageUrl is null
                ? "offered:    no package URL from the update feed or the AppX journal (raise the chain or pass --url)"
                : $"offered:    {check.OfferedVersion}  ({check.PackageUrl})");
            // no address known is not "an update exists" — the same rule as the GUI startup check
            Console.WriteLine(check.UpToDate ? "up to date — nothing to do"
                : check.OfferedVersion is null ? "update state unknown — no address to compare against"
                : "update available");
            return 0;
        }

        UpdateRunResult result;
        try
        {
            result = await runtime.Updater.RunAsync(options, progress, chainProgress, Interrupt.Token);
        }
        catch
        {
            // the chain may have been raised before the failure — never leave it half-up, and let
            // the stopper's re-arm spawn finish before this process exits
            await AwaitAutoStartQuietlyAsync(runtime);
            await HandOverProxyAsync(runtime, ChainLeftUpMessage);
            throw;
        }

        await runtime.WatchdogControl.LastAutoStart; // a raise re-arms the stopper — the spawn must survive this process
        // the update raises the chain on its own way to the package: this process holds the
        // in-process listener, and it would die with the command (the daemon and the keeper stay)
        var handedOver = await HandOverProxyAsync(runtime, ChainLeftUpMessage);
        switch (result.Outcome)
        {
            case UpdateOutcome.UpToDate:
                Console.WriteLine($"up to date: {result.Installed?.Version}");
                return 0;
            case UpdateOutcome.Downloaded:
                Console.WriteLine($"package ready: {result.PackageFile}");
                Console.WriteLine("install: pnr update --install-from <file>");
                return handedOver ? 0 : 1;
            case UpdateOutcome.Installed:
                Console.WriteLine($"done: {result.Installed?.Version} -> {result.InstalledAfter?.Version}");
                return handedOver ? 0 : 1;
            case UpdateOutcome.NoChange:
                // the run completed but the observation says the version is the same: a failure
                // unless the owner asked for a reinstall on purpose (--force accepts the reinstall
                // it asked for, never an update that did not land)
                (result.Reinstall ? Console.Out : Console.Error).WriteLine(result.Reinstall
                    ? $"reinstalled: {result.InstalledAfter?.Version} — the same version, as asked by --force"
                    : $"no change: {result.InstalledAfter?.Version} — the installed version did not reach the package's after the install");
                return handedOver && result.Reinstall ? 0 : 1;
            default:
                return 1;
        }
    }

    /// <summary>
    /// Direct-access guards: status (read-only), local ban of hosts/NRPT/firewall
    /// and the managed policy (both — UAC), env in Claude Code settings.json and the system-proxy
    /// flag in Codex config.toml (Windows; WSL separately), WinINET system proxy.
    /// After ban on/off — live checks (DNS/direct/proxied).
    /// </summary>
    private static async Task<int> RunGuardAsync(List<string> rest)
    {
        var action = rest.FirstOrDefault() ?? "status";
        rest = rest.Skip(1).ToList();
        var extra = action switch
        {
            "status" => rest.Where(a => a != "--live").ToList(),
            "settings" or "wsl" or "ban" or "policy" or "sysproxy" => rest.Skip(1).ToList(),
            _ => [],
        };
        if (extra.Count > 0)
        {
            Console.Error.WriteLine($"unexpected argument: {extra[0]} (pnr guard {action}{(action == "status" ? " [--live]" : "")})");
            return 1;
        }

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);

        switch (action)
        {
            case "status":
                var live = rest.Contains("--live");
                var snapshot = await runtime.Guard.GetStatusAsync(CancellationToken.None);
                foreach (var item in snapshot.Items.Where(item => item.Kind != GuardKind.SystemProxy))
                {
                    Console.WriteLine($"{Name(item.Kind),-15} {State(item.State)}  {item.Detail}");
                }

                Console.WriteLine($"{"sysproxy",-15} {State(snapshot[GuardKind.SystemProxy].State)}  {snapshot[GuardKind.SystemProxy].Detail}");
                if (!live)
                {
                    return 0;
                }

                Console.WriteLine();
                var checks = await runtime.Guard.LiveChecksAsync(Interrupt.Token);
                Console.WriteLine(checks.DnsDetail);
                Console.WriteLine(checks.DirectDetail);
                Console.WriteLine(checks.ProxiedDetail);
                // the verdict follows the ban's own state: with the ban on, 0 only when the DNS layer is
                // proven blocked and the direct probe found no open path (an unestablished verdict —
                // offline, no probes — is not "blocked"); with the ban intentionally off there is
                // nothing to hold against the probes
                return snapshot[GuardKind.LocalBan].State != GuardState.On || BanHolds(checks) ? 0 : 1;

            case "ban":
                bool? banOn = rest.FirstOrDefault() switch
                {
                    "on" => true,
                    "off" => false,
                    _ => null,
                };
                if (banOn is null)
                {
                    Console.Error.WriteLine("unknown action: pnr guard ban on|off");
                    return 1;
                }

                var ban = await runtime.Guard.BanAsync(banOn.Value, CancellationToken.None);
                Console.WriteLine(ban.Detail);
                Console.WriteLine(ban.Live.DnsDetail);
                Console.WriteLine(ban.Live.DirectDetail);
                Console.WriteLine(ban.Live.ProxiedDetail);
                // switching it on must leave the direct path closed, and the probes say whether it did
                return banOn.Value && !BanHolds(ban.Live) ? 1 : 0;

            case "policy":
                bool? policyOn = rest.FirstOrDefault() switch
                {
                    "on" => true,
                    "off" => false,
                    _ => null,
                };
                if (policyOn is null)
                {
                    Console.Error.WriteLine("unknown action: pnr guard policy on|off");
                    return 1;
                }

                Console.WriteLine(await runtime.Guard.PolicyAsync(policyOn.Value, CancellationToken.None));
                return 0;

            case "settings":
                if (rest.FirstOrDefault() != "sync")
                {
                    Console.Error.WriteLine("unknown action: pnr guard settings sync");
                    return 1;
                }

                var settings = await runtime.Guard.SyncSettingsAsync(CancellationToken.None);
                if (settings.ClaudeSkipped)
                {
                    Console.WriteLine("settings.json: skipped (claude.enabled=false)");
                }
                else if (settings.ClaudeError is not null)
                {
                    Console.Error.WriteLine($"settings.json: FAILED: {settings.ClaudeError}");
                }
                else
                {
                    Console.WriteLine(settings.Written
                        ? $"settings.json: written — proxy {settings.ProxyUrl}, NO_PROXY, privacy and attribution keys (backup: {settings.BackupPath ?? "none"})"
                        : $"settings.json: already up to date (proxy {settings.ProxyUrl})");
                }

                if (settings.CodexSkipped)
                {
                    Console.WriteLine("the Codex layer: skipped (codex.enabled=false)");
                }
                else if (settings.CodexError is not null)
                {
                    Console.Error.WriteLine($"the Codex layer: FAILED: {settings.CodexError}");
                }
                else
                {
                    Console.WriteLine(settings.CodexWritten
                        ? $"config.toml: respect_system_proxy = true written (backup: {settings.CodexBackupPath ?? "none"})"
                        : "config.toml: respect_system_proxy = true already in place");
                    Console.WriteLine(settings.CodexEnvWritten
                        ? $".env: proxy env written to {settings.CodexEnvPath} (backup: {settings.CodexEnvBackupPath ?? "none"}) — the file the Codex core loads itself"
                        : $".env: proxy env already in place ({settings.CodexEnvPath})");
                }

                return settings.ClaudeError is null && settings.CodexError is null ? 0 : 1;

            case "wsl":
                if (rest.FirstOrDefault() != "sync")
                {
                    Console.Error.WriteLine("unknown action: pnr guard wsl sync");
                    return 1;
                }

                var wsl = await runtime.Guard.SyncWslAsync(CancellationToken.None);
                if (wsl.LayerDisabled)
                {
                    Console.WriteLine($"wsl sync skipped: {wsl.Reason}");
                    return 0;
                }

                foreach (var distro in wsl.Distros)
                {
                    if (distro.Failed)
                    {
                        Console.Error.WriteLine($"{distro.Distro}: {distro.Reason}");
                        continue;
                    }

                    if (distro.Skipped)
                    {
                        Console.WriteLine($"{distro.Distro}: skipped ({distro.Reason})");
                        continue;
                    }

                    Console.WriteLine(distro.SettingsWritten || distro.ScriptWritten || distro.ProxyEnvWritten
                        || distro.BootRunnerWritten || distro.UserCommandWritten || distro.WslConfWritten
                        ? $"{distro.Distro}: proxy {distro.ProxyUrl}, settings.json + startup script {(distro.ProxyEnvWritten ? "+ codex proxy env " : "")}written"
                        : $"{distro.Distro}: proxy {distro.ProxyUrl}, already up to date");
                    if (distro.ClaudeDirChowned)
                    {
                        Console.WriteLine($"{distro.Distro}: ~/.claude belonged to root, handed back to the user");
                    }
                }

                return wsl.Distros.Any(d => d.Failed) ? 1 : 0;

            case "sysproxy":
                bool? sysOn = rest.FirstOrDefault() switch
                {
                    "on" => true,
                    "off" => false,
                    _ => null,
                };
                if (sysOn is null)
                {
                    Console.Error.WriteLine("unknown action: pnr guard sysproxy on|off");
                    return 1;
                }

                Console.WriteLine(await runtime.Guard.SetSystemProxyAsync(sysOn.Value, CancellationToken.None));
                return 0;

            default:
                Console.Error.WriteLine($"unknown action: {action} (status [--live] | ban on|off | policy on|off | settings sync | wsl sync | sysproxy on|off)");
                return 1;
        }

        static string Name(GuardKind kind) => kind switch
        {
            GuardKind.LocalBan => "local ban",
            GuardKind.EgressPolicy => "egress policy",
            GuardKind.ClaudeSettings => "settings.json",
            GuardKind.CodexSettings => "codex config",
            GuardKind.WslConfig => "wsl config",
            GuardKind.SystemProxy => "sysproxy",
            _ => kind.ToString().ToLowerInvariant(),
        };

        static string State(GuardState state) => state switch
        {
            GuardState.On => "on     ",
            GuardState.Off => "off    ",
            GuardState.Partial => "partial",
            _ => "unknown",
        };
    }

    private static int ConfigCommand(List<string> rest)
    {
        var action = rest.FirstOrDefault() ?? "path";
        if (RejectExtraArguments("pnr config path|init", rest, 1))
        {
            return 1;
        }

        switch (action)
        {
            case "path":
                Console.WriteLine(AppConfigStore.GetDefaultConfigPath());
                return 0;
            case "init":
                var config = AppConfigStore.LoadOrCreate();
                Console.WriteLine(AppConfigStore.GetDefaultConfigPath());
                Console.WriteLine(config.ForwardProxy.Enabled
                    ? "created (or already present) with default values"
                    : "created (or already present); forward proxy is disabled");
                return 0;
            default:
                Console.Error.WriteLine($"unknown action: {action} (path | init)");
                return 1;
        }
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Synchronous IProgress: without a synchronization context Progress&lt;T&gt; would print out of order from the thread pool.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }

    /// <summary>The version of the running build (the part before "+" of the informational version is the release).</summary>
    private static int Version()
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(a => a.InformationalVersion)
            .FirstOrDefault() ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        Console.WriteLine($"pnr {(plus > 0 ? version[..plus] : version)}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command} (pnr help lists the commands)");
        return 1;
    }

    /// <summary>
    /// A command that takes at most <paramref name="allowed"/> positional arguments: anything past
    /// them (a misspelled flag included) is an error instead of being silently ignored.
    /// </summary>
    private static bool RejectExtraArguments(string usage, List<string> rest, int allowed)
    {
        if (rest.Count <= allowed)
        {
            return false;
        }

        Console.Error.WriteLine($"unexpected argument: {rest[allowed]} ({usage})");
        return true;
    }

    /// <summary>True when the live probes show the ban working: DNS proven blocked and no open direct path.</summary>
    private static bool BanHolds(BanLiveChecks checks) => checks.DnsBlocked == true && checks.DirectBlocked != false;

    private static int Help()
    {
        Console.WriteLine("""
            pnr — PrivateNetRouting headless mode

            pnr proxy [--detached]
                                 run the forward proxy in this console (Ctrl+C to stop);
                                 --detached starts the detached worker instead and
                                 returns — the worker survives this command
                                 (forwardProxy.detached)
            pnr chain status     chain state; tailscale mode: daemon/keeper/node/proxy/health,
                                 socks5 and http modes: upstream/proxy/health (chain.mode)
            pnr chain up         bring the chain up (tailscale: daemon -> keeper -> node -> proxy;
                                 socks5: egress check -> proxy, or straight to the health check
                                 when forwardProxy.enabled=false; http: egress check -> health
                                 check, no local proxy; UAC only in the tailscale mode and only
                                 when the daemon runs elevated, see chain.tailscale.elevation)
            pnr chain down       stop the chain (tailscale: down -> daemon -> keeper -> proxy, UAC
                                 only for an elevated daemon; in the socks5 mode only the proxy
                                 stops, in the skipped shapes (http, or socks5 with
                                 forwardProxy.enabled=false) nothing of ours is running)
            pnr check            check the chain with a CONNECT-roundtrip
            pnr status           read-only state summary:
                                 chain processes and port owners (pid), WSL bind, proxy
                                 counters, activity age, CONNECT timing, policy and ban,
                                 idle supervisor; a stopped chain is a short notice
            pnr logs list        log files of logging.directory with sizes and dates
            pnr logs show <name> [--tail N]
                                 last N lines of a log (default 50; name with or
                                 without .log, see pnr logs list)
            pnr logs rotate [<name>...]
                                 rotate log files (current -> .1 -> ...); a file held
                                 by the running app is reported as locked
            pnr launch [--check-only] [--no-splash] [--restart]
                                 start Claude with --proxy-server/--proxy-bypass-list,
                                 bringing the chain up if needed (splash is GUI-only);
                                 --restart closes a copy already running without our proxy
                                 and starts it again through the proxy
            pnr browser [--check-only] [--private] [--yes] [--url <url>]
                                 start the browser with the profile created once through the
                                 application ("?" -> "Create browser profile..."); the traffic
                                 goes through the egress chosen by browser.egress: forward-proxy
                                 (default, the applications proxy endpoint over HTTP CONNECT) or
                                 chain (the chain's final egress, independent of the application
                                 process); --check-only only reports whether the egress answers
                                 (exit code 1 when it does not), --yes raises the chain / starts
                                 anyway without the console question
            pnr codex launch [--check-only] [--restart]
                                 start the Codex desktop app (OpenAI, Microsoft Store MSIX)
                                 with --proxy-server/--proxy-bypass-list and the proxy env
                                 (package-context launch, codex.* config), bringing the
                                 chain up if needed; --restart closes a copy already running
                                 without our proxy and starts it again
            pnr codex update [--check]
                                 update the app through the Microsoft Store: the Store catalog
                                 is asked first (no chain, no UAC), a running Codex is closed
                                 before the Store replaces the package
            pnr update [--check] [--download-only] [--force] [--keep-file]
                       [--keep-running] [--url <url>] [--install-from <path>]
                                 update Claude Desktop (MSIX) through the proxy chain:
                                 versions are checked first (no UAC when up to date),
                                 then chain if needed, fragment download, publisher
                                 check and Add-AppxPackage; --install-from installs a
                                 downloaded file and excludes --check/--download-only
            pnr watchdog         idle auto-stop supervisor: check Claude and Codex usage
                                 every watchdog.checkSeconds, on idle stop the WSL host
                                 and the chain (Ctrl+C to stop; starts nothing by itself)
            pnr guard status [--live]
                                 guard states, read-only: local ban (hosts/NRPT/firewall),
                                 egress policy, Claude settings.json env, Codex config.toml,
                                 WSL config; --live adds DNS/direct/proxied probes
            pnr guard ban on|off local ban on direct access: hosts + NRPT + firewall (UAC)
            pnr guard policy on|off
                                 managed policy egressProxyUrl + disableWslSessions=0 (UAC)
            pnr guard settings sync
                                 write env (proxy + NO_PROXY) into Claude Code settings.json,
                                 [features] respect_system_proxy into Codex config.toml and
                                 the proxy env into the .env of CODEX_HOME
            pnr guard wsl sync   update the WSL layer of the distros (wsl.distros): the startup
                                 script, settings.json env, the Codex proxy env snippet and
                                 the boot runner in /etc/wsl.conf
            pnr guard sysproxy on|off
                                 system WinINET proxy on the app's forward proxy (no UAC)
            pnr config path      user config path
            pnr config init      create the default config if missing
            pnr --version        print the version
            """);
        return 0;
    }
}
