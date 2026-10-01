using PrivateNetRouting.Core;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Cli;

/// <summary>
/// Headless-режим (паритет со старыми скриптами): proxy — крутить forward-прокси в консоли,
/// check — CONNECT-roundtrip, config — путь/инициализация пользовательского конфига.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault() ?? "help";
        try
        {
            return command switch
            {
                "proxy" => await RunProxyAsync(),
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

    private static int Help()
    {
        Console.WriteLine("""
            pnr — headless-режим PrivateNetRouting

            pnr proxy          запустить forward-прокси (Ctrl+C — стоп)
            pnr check          проверить цепочку CONNECT-roundtrip'ом
            pnr config path    путь пользовательского конфига
            pnr config init    создать конфиг по умолчанию, если его нет
            """);
        return 0;
    }
}
