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

    /// <summary>Node BackendState from status --json; null — the CLI did not respond.</summary>
    Task<string?> GetBackendStateAsync(CancellationToken ct);

    Task<CliResult> UpAsync(CancellationToken ct);

    Task<CliResult> DownAsync(CancellationToken ct);
}

/// <summary>
/// Wrapper around `tailscale --socket &lt;pipe&gt; …` (a direct launch).
/// </summary>
public sealed partial class SidecarCli(ChainTailscaleConfig config) : ISidecarCli
{
    [GeneratedRegex(@"""BackendState"":\s*""([^""]+)""")]
    private static partial Regex BackendStateRegex();

    /// <summary>On top of upTimeoutSeconds: the CLI's own --timeout must fire first, the wrapper limit is a safety net.</summary>
    private const int UpTimeoutMarginSeconds = 10;

    public async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var command = arguments.Count > 0 ? arguments[0] : "cli";
        // a daemon that stopped answering its pipe leaves the CLI hanging forever — a hard limit
        // turns that into an error (and kills the process) instead of a hung raise/stop/poll
        var timeout = TimeSpan.FromSeconds(
            command == "up" ? config.UpTimeoutSeconds + UpTimeoutMarginSeconds : config.CliTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var info = new ProcessStartInfo(config.CliPath)
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
            ?? throw new IOException($"failed to start {config.CliPath}");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = (await stdoutTask + await stderrTask).Trim();
            return new CliResult(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // our own limit fired, not the caller's cancellation
            throw new IOException(
                $"tailscale {command} did not exit within {(int)timeout.TotalSeconds} s — the daemon pipe is unresponsive");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // lost the race with the process exiting on its own — nothing to kill
                }
            }
        }
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
        // set-like flag set only: up overwrites the unspecified with defaults
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
