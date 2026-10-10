using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.Cli;

/// <summary>
/// Headless mode: proxy — runs the forward proxy in the console
/// (or brings up a detached worker), chain — chain control (status/up/down), check —
/// CONNECT roundtrip, status — diagnostics summary, logs —
/// viewing/rotation of logs, config — path/initialization. Elevated commands (--elevated-*)
/// are executed by the internal helper and it exits; --proxy-worker — the body of the detached proxy worker.
/// </summary>
internal static class Program
{
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
            return await ProxyWorker.RunAsync(stop.Token);
        }

        if (first is ElevatedChainCommands.StartDaemon or ElevatedChainCommands.StopChain || GuardElevatedCommands.IsCommand(first))
        {
            var resultFile = ParseOption(args, "--result-file")
                ?? throw new ArgumentException("elevated helper requires --result-file");
            return GuardElevatedCommands.IsCommand(first)
                ? GuardElevatedCommands.Execute(first, resultFile)
                : ElevatedChainCommands.Execute(first, resultFile);
        }

        try
        {
            return first switch
            {
                "proxy" => await RunProxyAsync(args.Skip(1).ToList()),
                "chain" => await RunChainAsync(args.Skip(1).ToList()),
                "check" => await RunCheckAsync(),
                "status" => await RunStatusAsync(),
                "logs" => RunLogsCommand(args.Skip(1).ToList()),
                "launch" => await RunLaunchAsync(args.Skip(1).ToList()),
                "browser" => await RunBrowserAsync(args.Skip(1).ToList()),
                "update" => await RunUpdateAsync(args.Skip(1).ToList()),
                "watchdog" => await RunWatchdogAsync(),
                "guard" => await RunGuardAsync(args.Skip(1).ToList()),
                "config" => ConfigCommand(args.Skip(1).ToList()),
                _ => Help(),
            };
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

        await using var consoleRuntime = ProxyRuntime.FromConfig(config);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        await consoleRuntime.ProxyService.StartAsync(stop.Token);
        Console.WriteLine($"forward proxy: listening on {string.Join(", ", consoleRuntime.ProxyService.ListenerEndpoints)}");
        Console.WriteLine($"log: {consoleRuntime.Log.FilePath}");
        Console.WriteLine("Ctrl+C to stop");
        await Task.Delay(Timeout.Infinite, stop.Token);
        return 0;
    }

    private static async Task<int> RunChainAsync(List<string> rest)
    {
        var action = rest.FirstOrDefault() ?? "status";
        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        var progress = new Progress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));

        switch (action)
        {
            case "status":
                var state = await runtime.Chain.GetStateAsync(CancellationToken.None);
                if (config.Chain.EffectiveMode == ChainMode.Socks5)
                {
                    var upstream = string.IsNullOrEmpty(config.ForwardProxy.Socks5UpstreamUser)
                        ? config.ForwardProxy.Socks5Upstream
                        : $"{config.ForwardProxy.Socks5UpstreamUser}@{config.ForwardProxy.Socks5Upstream}";
                    Console.WriteLine($"upstream:  {upstream}, {(state.UpstreamReachable is true ? "reachable" : state.UpstreamReachable is false ? "no route" : "not checked")}");
                }
                else
                {
                    Console.WriteLine($"daemon:    listener {(state.DaemonHttpListening ? "up" : "down")}, process {(state.DaemonProcessAlive ? "alive" : "not found")}");
                    Console.WriteLine($"keeper:    {(state.KeeperRunning ? "running" : "not found")}");
                    Console.WriteLine($"node:      BackendState={state.BackendState ?? "no response"}");
                }

                Console.WriteLine($"proxy:     {(state.ProxyRunning ? $"running ({string.Join(", ", runtime.ProxyControl.ListenerEndpoints)})" : "stopped")}, health {(state.ProxyHealthy ? "alive" : "failed")}");
                Console.WriteLine(state.IsFullyUp ? "chain: fully up" : state.IsStopped ? "chain: stopped" : "chain: partial");
                return 0;

            case "up":
                await runtime.Chain.EnsureRunningAsync(progress, CancellationToken.None);
                Console.WriteLine("chain is up");
                await runtime.WatchdogControl.LastAutoStart; // the re-arm spawn must survive this process
                return 0;

            case "down":
                await runtime.Chain.StopAsync(progress, CancellationToken.None);
                Console.WriteLine("chain is stopped");
                return 0;

            default:
                Console.Error.WriteLine($"unknown action: {action} (status | up | down)");
                return 1;
        }
    }

    private static async Task<int> RunCheckAsync()
    {
        var config = AppConfigStore.LoadOrCreate();
        var fp = config.ForwardProxy;
        var endpoint = Socks5Client.ParseEndPoint(fp.Listen);
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
    /// a short message: this is normal while Claude is not needed.
    /// </summary>
    private static async Task<int> RunStatusAsync()
    {
        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        var summary = await runtime.Diagnostics.GetSummaryAsync(CancellationToken.None);
        if (summary.ChainStopped)
        {
            Console.WriteLine($"chain ({config.ForwardProxy.Listen}) is stopped — normal while Claude is not needed.");
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
    /// shifting; nothing is started and no config is created). A file opened by the application is rotated
    /// from its diagnostics window — here it will be marked as locked.
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

    private static async Task<int> RunLaunchAsync(List<string> rest)
    {
        var checkOnly = false;
        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--check-only":
                    checkOnly = true;
                    break;
                case "--no-splash":
                    break; // the bring-up is drawn by the GUI window; accepted for shortcut compatibility
                default:
                    Console.Error.WriteLine($"unknown option: {argument} (--check-only | --no-splash)");
                    return 1;
            }
        }

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        var progress = new Progress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));

        var result = await runtime.Launcher.LaunchAsync(new LauncherOptions(checkOnly), progress, CancellationToken.None);
        await runtime.WatchdogControl.LastAutoStart; // the re-arm spawn must survive this process
        if (result.Outcome == LaunchOutcome.CheckOnly)
        {
            Console.WriteLine(result.ChainRaised ? "OK: chain is up" : "OK: chain healthy");
            return 0;
        }

        Console.WriteLine($"claude launched (proxy {runtime.Launcher.ProxyUrl}, chain raised: {result.ChainRaised})");
        return 0;
    }

    private static async Task<int> RunBrowserAsync(List<string> rest)
    {
        var options = BrowserLauncherOptions.Default;
        string? url = null;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--check-only":
                    options = options with { CheckOnly = true };
                    break;
                case "--prepare-only":
                    options = options with { PrepareOnly = true };
                    break;
                case "--private":
                    options = options with { Private = true };
                    break;
                case "--url":
                    if (i + 1 >= rest.Count)
                    {
                        Console.Error.WriteLine("--url requires a value");
                        return 1;
                    }

                    url = rest[++i];
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {rest[i]} (--check-only | --prepare-only | --private | --url <url>)");
                    return 1;
            }
        }

        if (url is not null)
        {
            options = options with { Url = url };
        }

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        if (!options.CheckOnly && runtime.Browser.IsOurBrowserRunning())
        {
            Console.WriteLine("browser is already running with our profile — bringing it up is up to the taskbar");
            return 0;
        }

        var progress = new Progress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));
        var result = await runtime.Browser.LaunchAsync(options, progress, CancellationToken.None);
        await runtime.WatchdogControl.LastAutoStart; // the re-arm spawn must survive this process
        if (result.Outcome != BrowserLaunchOutcome.Launched)
        {
            Console.WriteLine(result.ChainRaised ? "OK: chain is up" : "OK: chain healthy");
            return 0;
        }

        Console.WriteLine($"browser launched (proxy {runtime.Browser.ProxyUrl}, chain raised: {result.ChainRaised})");
        return 0;
    }

    private static async Task<int> RunWatchdogAsync()
    {
        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        // the stopper exists to stop a live chain: over a down one it would only wait forever
        // (the usage probe sees the running Claude and never reports idle)
        var state = await runtime.Chain.GetStateAsync(CancellationToken.None);
        if (!state.IsFullyUp)
        {
            Console.WriteLine("watchdog: the chain is not up — nothing to stop; raise the chain first (pnr chain up)");
            return 1;
        }

        Console.WriteLine($"watchdog: checking Claude usage every {config.Watchdog.CheckSeconds}s; Ctrl+C to stop");
        if (string.IsNullOrWhiteSpace(config.Watchdog.WslDistro))
        {
            Console.WriteLine("watchdog: watchdog.wslDistro is not set, the WSL host will not be stopped on idle");
        }

        var progress = new Progress<ChainStep>(step =>
            Console.WriteLine($"[{step.Status,-7}] {step.Component}: {step.Detail}"));
        try
        {
            await runtime.Watchdog.RunAsync(progress, stop.Token);
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
                    if (i + 1 >= rest.Count)
                    {
                        Console.Error.WriteLine("--url requires a value");
                        return 1;
                    }

                    options = options with { ManualUrl = rest[++i] };
                    break;
                case "--install-from":
                    if (i + 1 >= rest.Count)
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

        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        var progress = new SyncProgress<UpdateProgress>(p =>
            Console.WriteLine(p.TotalBytes > 0 && p.Phase == UpdatePhase.Download
                ? $"  download {ByteSize.Format(p.DoneBytes)} of {ByteSize.Format(p.TotalBytes)}"
                : $"[{p.Phase.ToString().ToLowerInvariant()}] {p.Message}"));
        var chainProgress = new SyncProgress<ChainStep>(step =>
            Console.WriteLine($"  [{step.Status,-7}] {step.Component}: {step.Detail}"));

        if (installFrom is not null)
        {
            var after = await runtime.Updater.InstallAsync(installFrom, options, progress, CancellationToken.None);
            Console.WriteLine($"installed: {after.Version}");
            return 0;
        }

        if (checkOnly)
        {
            var check = await runtime.Updater.CheckAsync(options, CancellationToken.None);
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

        var result = await runtime.Updater.RunAsync(options, progress, chainProgress, CancellationToken.None);
        switch (result.Outcome)
        {
            case UpdateOutcome.UpToDate:
                Console.WriteLine($"up to date: {result.Installed?.Version}");
                return 0;
            case UpdateOutcome.Downloaded:
                Console.WriteLine($"package ready: {result.PackageFile}");
                Console.WriteLine("install: pnr update --install-from <file>");
                return 0;
            case UpdateOutcome.Installed:
                Console.WriteLine($"done: {result.Installed?.Version} -> {result.InstalledAfter?.Version}");
                return 0;
            default:
                return 1;
        }
    }

    /// <summary>
    /// Direct-access guards: status (read-only), local ban of hosts/NRPT/firewall
    /// and the managed policy (both — UAC), env in Claude Code settings.json (Windows and WSL),
    /// WinINET system proxy. After ban on/off — live checks (DNS/direct/proxied).
    /// </summary>
    private static async Task<int> RunGuardAsync(List<string> rest)
    {
        var action = rest.FirstOrDefault() ?? "status";
        rest = rest.Skip(1).ToList();
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

                Console.WriteLine($"{"sysproxy",-15} {State(snapshot.SystemProxy.Enabled ? GuardState.On : GuardState.Off)}  {snapshot[GuardKind.SystemProxy].Detail}");
                if (!live)
                {
                    return 0;
                }

                Console.WriteLine();
                var checks = await runtime.Guard.LiveChecksAsync(CancellationToken.None);
                Console.WriteLine(checks.DnsDetail);
                Console.WriteLine(checks.DirectDetail);
                Console.WriteLine(checks.ProxiedDetail);
                return checks.DnsBlocked && checks.DirectBlocked ? 0 : 1;

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
                return 0;

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
                Console.WriteLine(settings.Written
                    ? $"settings.json: env proxy {settings.ProxyUrl}, NO_PROXY written (backup: {settings.BackupPath ?? "none"})"
                    : $"settings.json: env already up to date (proxy {settings.ProxyUrl})");
                return 0;

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
                    if (distro.Skipped)
                    {
                        Console.WriteLine($"{distro.Distro}: skipped ({distro.Reason})");
                        continue;
                    }

                    Console.WriteLine(distro.SettingsWritten || distro.ScriptWritten
                        ? $"{distro.Distro}: proxy {distro.ProxyUrl}, settings.json + startup script written"
                        : $"{distro.Distro}: proxy {distro.ProxyUrl}, already up to date");
                }

                return 0;

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

                await runtime.Guard.SetSystemProxyAsync(sysOn.Value, CancellationToken.None);
                Console.WriteLine(sysOn.Value
                    ? $"system proxy enabled: {config.ForwardProxy.Listen} (bypass: corp domains + local ranges)"
                    : "system proxy disabled (direct access restored)");
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
                                 socks5 mode: upstream/proxy/health (chain.mode)
            pnr chain up         bring the chain up (tailscale: daemon -> keeper -> node -> proxy;
                                 socks5: egress check -> proxy; UAC only in the tailscale mode)
            pnr chain down       stop the chain (down -> daemon -> keeper -> proxy; UAC; in the
                                 socks5 mode only the proxy stops)
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
            pnr launch [--check-only] [--no-splash]
                                 start Claude with --proxy-server/--proxy-bypass-list,
                                 bringing the chain up if needed (splash is GUI-only)
            pnr browser [--check-only] [--prepare-only] [--private] [--url <url>]
                                 start the browser with an isolated profile and the app's
                                 proxy (firefox user.js / chromium flags, browser.* config);
                                 --prepare-only writes the profile without opening it
            pnr update [--check] [--download-only] [--force] [--keep-file]
                       [--keep-running] [--url <url>] [--install-from <path>]
                                 update Claude Desktop (MSIX) through the proxy chain:
                                 versions are checked first (no UAC when up to date),
                                 then chain if needed, fragment download, publisher
                                 check and Add-AppxPackage
            pnr watchdog         idle auto-stop supervisor: check Claude usage every
                                 watchdog.checkSeconds, on idle stop the WSL host and
                                 the chain (Ctrl+C to stop; starts nothing by itself)
            pnr guard status [--live]
                                 guard states, read-only: local ban (hosts/NRPT/firewall),
                                 egress policy, Claude settings.json env, WSL config;
                                 --live adds DNS/direct/proxied probes
            pnr guard ban on|off local ban on direct access: hosts + NRPT + firewall (UAC)
            pnr guard policy on|off
                                 managed policy egressProxyUrl + disableWslSessions=0 (UAC)
            pnr guard settings sync
                                 write env (proxy + NO_PROXY) into Claude Code settings.json
            pnr guard wsl sync   update the WSL startup script and settings.json env in
                                 the distro (guard.wslDistro/guard.wslUser)
            pnr guard sysproxy on|off
                                 system WinINET proxy on the app's forward proxy (no UAC)
            pnr config path      user config path
            pnr config init      create the default config if missing
            """);
        return 0;
    }
}
