using System.Diagnostics;
using System.Text.RegularExpressions;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Chain;

public sealed record CliResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

public interface ISidecarCli
{
    Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>BackendState узла из status --json; null — CLI не ответил.</summary>
    Task<string?> GetBackendStateAsync(CancellationToken ct);

    Task<CliResult> UpAsync(CancellationToken ct);

    Task<CliResult> DownAsync(CancellationToken ct);
}

/// <summary>
/// Обёртка `tailscale --socket &lt;pipe&gt; …` (в прежней схеме для этого существовал
/// ts-sidecar-cli.ps1 — из bash пайп-пути ломались; здесь прямой запуск).
/// </summary>
public sealed partial class SidecarCli(ChainConfig config) : ISidecarCli
{
    [GeneratedRegex(@"""BackendState"":\s*""([^""]+)""")]
    private static partial Regex BackendStateRegex();

    public async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(config.TailscaleCliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--socket");
        info.ArgumentList.Add(config.Socket);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new IOException($"не удалось запустить {config.TailscaleCliPath}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = (await stdoutTask + await stderrTask).Trim();
        return new CliResult(process.ExitCode, output);
    }

    public async Task<string?> GetBackendStateAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(["status", "--json"], ct);
            var match = BackendStateRegex().Match(result.Output);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task<CliResult> UpAsync(CancellationToken ct)
    {
        // только set-подобный набор флагов: up перезаписывает неуказанное дефолтами (правило прежней схемы)
        var arguments = new List<string>
        {
            "up",
            "--accept-dns=false",
            "--accept-routes",
            $"--timeout={config.UpTimeoutSeconds}s",
        };
        if (!string.IsNullOrWhiteSpace(config.LoginServer))
        {
            arguments.Add($"--login-server={config.LoginServer}");
        }

        return await RunAsync(arguments, ct);
    }

    public Task<CliResult> DownAsync(CancellationToken ct) => RunAsync(["down"], ct);
}
