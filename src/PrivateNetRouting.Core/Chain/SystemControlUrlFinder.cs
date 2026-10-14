using System.Diagnostics;
using System.Text.RegularExpressions;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Chain;

public interface ISystemControlUrlFinder
{
    /// <summary>Coordination server of the machine's system-wide tailscale; null — not available.</summary>
    Task<string?> FindAsync(CancellationToken ct);
}

/// <summary>
/// Reads the coordination server of the system-wide tailscale (the daemon on the default named
/// pipe) out of `tailscale debug prefs` ("ControlURL") — a read-only call. Used once, when the
/// sidecar node logs in for the first time and chain.tailscale.loginServer is empty: a fresh
/// statedir knows no control server, and the tailnet this machine already uses is the right
/// guess. The system service does not have to answer: the finder then returns null and the
/// login goes to the default (public) control server.
/// </summary>
public sealed partial class SystemControlUrlFinder(ChainTailscaleConfig config, FileLog log) : ISystemControlUrlFinder
{
    [GeneratedRegex(@"""ControlURL""\s*:\s*""([^""]+)""")]
    private static partial Regex ControlUrlRegex();

    public async Task<string?> FindAsync(CancellationToken ct)
    {
        var info = new ProcessStartInfo(config.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // no --socket: the default pipe belongs to the system-wide daemon
        info.ArgumentList.Add("debug");
        info.ArgumentList.Add("prefs");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.CliTimeoutSeconds));
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                log.Debug("system tailscale: failed to start the CLI");
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var url = ParseControlUrl(await stdoutTask + await stderrTask);
            log.Debug(url is null
                ? "system tailscale: no ControlURL in the prefs (the daemon is not running, or the output is unexpected)"
                : "system tailscale: control server picked up from the prefs");
            return url;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // our own limit fired — no system-wide tailscale to steal from, a normal case
            log.Debug("system tailscale: control server lookup did not answer within the limit");
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            log.Debug($"system tailscale: control server lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses "ControlURL" out of the `debug prefs` JSON; null — nothing found.</summary>
    internal static string? ParseControlUrl(string output) => ControlUrlRegex().Match(output) is { Success: true } match
        ? match.Groups[1].Value
        : null;
}
