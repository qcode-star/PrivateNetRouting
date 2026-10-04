using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Launcher;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.Cli;

/// <summary>
/// Headless-режим (паритет со старыми скриптами): proxy — крутить forward-прокси в консоли,
/// chain — управление цепочкой (status/up/down), check — CONNECT-roundtrip, config — путь/инициализация.
/// Элевейтнутые команды (--elevated-*) выполняет внутренний helper и выходит.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var first = args.FirstOrDefault() ?? "help";
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
                "proxy" => await RunProxyAsync(),
                "chain" => await RunChainAsync(args.Skip(1).ToList()),
                "check" => await RunCheckAsync(),
                "launch" => await RunLaunchAsync(args.Skip(1).ToList()),
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

    private static async Task<int> RunProxyAsync()
    {
        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        if (!config.ForwardProxy.Enabled)
        {
            Console.WriteLine("forward proxy is disabled in config (forwardProxy.enabled=false)");
            return 0;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        await runtime.StartAsync(stop.Token);
        Console.WriteLine($"forward proxy: listening on {string.Join(", ", runtime.Proxy.ListenerEndpoints)}");
        Console.WriteLine($"log: {runtime.Log.FilePath}");
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
                Console.WriteLine($"daemon:    listener {(state.DaemonHttpListening ? "up" : "down")}, process {(state.DaemonProcessAlive ? "alive" : "not found")}");
                Console.WriteLine($"keeper:    {(state.KeeperRunning ? "running" : "not found")}");
                Console.WriteLine($"node:      BackendState={state.BackendState ?? "no response"}");
                Console.WriteLine($"proxy:     {(state.ProxyRunning ? $"running ({string.Join(", ", runtime.Proxy.ListenerEndpoints)})" : "stopped")}, health {(state.ProxyHealthy ? "alive" : "failed")}");
                Console.WriteLine(state.IsFullyUp ? "chain: fully up" : "chain: partial");
                return 0;

            case "up":
                await runtime.Chain.EnsureRunningAsync(progress, CancellationToken.None);
                Console.WriteLine("chain is up");
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
                    break; // заставка существует только в GUI-режиме; флаг принят для совместимости ярлыков
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
        if (result.Outcome == LaunchOutcome.CheckOnly)
        {
            Console.WriteLine(result.ChainRaised ? "OK: chain is up" : "OK: chain healthy");
            return 0;
        }

        Console.WriteLine($"claude launched (proxy {runtime.Launcher.ProxyUrl}, chain raised: {result.ChainRaised})");
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
    /// Ручное обновление Claude Desktop (MSIX) через прокси-цепочку: порт ключей прежнего
    /// движка обновления. Проверка версий идёт до подъёма цепочки — «нечего ставить»
    /// не поднимает ничего и не просит UAC.
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
                ? "offered:    no fresh package URL in the AppX journal (launch Claude Desktop or pass --url)"
                : $"offered:    {check.OfferedVersion}  ({check.PackageUrl})");
            Console.WriteLine(check.UpToDate ? "up to date — nothing to do" : "update available");
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
    /// Гарды прямого доступа: статус (только чтение), локальный бан hosts/NRPT/firewall
    /// и managed-политика (обе — UAC), env в settings.json Claude Code (Windows и WSL),
    /// системный прокси WinINET. После ban on/off — живые проверки (DNS/прямое/проксируемое).
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
                Console.WriteLine($"settings.json: env proxy {settings.ProxyUrl}, NO_PROXY written (backup: {settings.BackupPath ?? "none"})");
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
                    Console.WriteLine(distro.Skipped
                        ? $"{distro.Distro}: skipped ({distro.Reason})"
                        : $"{distro.Distro}: proxy {distro.ProxyUrl}, settings.json + startup script written");
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

    /// <summary>Синхронный IProgress: без контекста синхронизации Progress&lt;T&gt; печатал бы из пула потоков не по порядку.</summary>
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

            pnr proxy            run the forward proxy (Ctrl+C to stop)
            pnr chain status     chain state (daemon/keeper/node/proxy/health)
            pnr chain up         bring the chain up (daemon -> keeper -> node -> proxy; UAC)
            pnr chain down       stop the chain (down -> daemon -> keeper -> proxy; UAC)
            pnr check            check the chain with a CONNECT-roundtrip
            pnr launch [--check-only] [--no-splash]
                                 start Claude with --proxy-server/--proxy-bypass-list,
                                 bringing the chain up if needed (splash is GUI-only)
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
