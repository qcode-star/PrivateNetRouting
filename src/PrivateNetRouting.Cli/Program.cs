using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Net.Socks;

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
        if (first is ElevatedChainCommands.StartDaemon or ElevatedChainCommands.StopChain)
        {
            var resultFile = ParseOption(args, "--result-file")
                ?? throw new ArgumentException("элевейтнутому helper нужен --result-file");
            return ElevatedChainCommands.Execute(first, resultFile);
        }

        try
        {
            return first switch
            {
                "proxy" => await RunProxyAsync(),
                "chain" => await RunChainAsync(args.Skip(1).ToList()),
                "check" => await RunCheckAsync(),
                "config" => ConfigCommand(args.Skip(1).ToList()),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ошибка: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunProxyAsync()
    {
        var config = AppConfigStore.LoadOrCreate();
        await using var runtime = ProxyRuntime.FromConfig(config);
        if (!config.ForwardProxy.Enabled)
        {
            Console.WriteLine("forward-прокси выключен в конфиге (forwardProxy.enabled=false)");
            return 0;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        await runtime.StartAsync(stop.Token);
        Console.WriteLine($"forward-прокси: слушаю {string.Join(", ", runtime.Proxy.ListenerEndpoints)}");
        Console.WriteLine($"лог: {runtime.Log.FilePath}");
        Console.WriteLine("Ctrl+C — остановить");
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
                Console.WriteLine($"демон:      слушатель {(state.DaemonHttpListening ? "есть" : "нет")}, процесс {(state.DaemonProcessAlive ? "жив" : "нет")}");
                Console.WriteLine($"кипер:      {(state.KeeperRunning ? "работает" : "нет")}");
                Console.WriteLine($"узел:       BackendState={state.BackendState ?? "нет ответа"}");
                Console.WriteLine($"прокси:     {(state.ProxyRunning ? $"работает ({string.Join(", ", runtime.Proxy.ListenerEndpoints)})" : "остановлен")}, health {(state.ProxyHealthy ? "жив" : "не прошёл")}");
                Console.WriteLine(state.IsFullyUp ? "цепочка: полностью поднята" : "цепочка: не полностью");
                return 0;

            case "up":
                await runtime.Chain.EnsureRunningAsync(progress, CancellationToken.None);
                Console.WriteLine("цепочка поднята");
                return 0;

            case "down":
                await runtime.Chain.StopAsync(progress, CancellationToken.None);
                Console.WriteLine("цепочка остановлена");
                return 0;

            default:
                Console.Error.WriteLine($"неизвестное действие: {action} (status | up | down)");
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
        Console.WriteLine(alive ? "цепочка жива" : "цепочка не отвечает");
        return alive ? 0 : 2;
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
                    ? "создан (или уже был) со значениями по умолчанию"
                    : "создан (или уже был); forward-прокси выключен");
                return 0;
            default:
                Console.Error.WriteLine($"неизвестное действие: {action} (path | init)");
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

    private static int Help()
    {
        Console.WriteLine("""
            pnr — headless-режим PrivateNetRouting

            pnr proxy            запустить forward-прокси (Ctrl+C — стоп)
            pnr chain status     состояние цепочки (демон/кипер/узел/прокси/health)
            pnr chain up         поднять цепочку (демон → кипер → узел → прокси; UAC)
            pnr chain down       остановить цепочку (down → демон → кипер → прокси; UAC)
            pnr check            проверить цепочку CONNECT-roundtrip'ом
            pnr config path      путь пользовательского конфига
            pnr config init      создать конфиг по умолчанию, если его нет
            """);
        return 0;
    }
}
