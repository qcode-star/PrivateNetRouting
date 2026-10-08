using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PrivateNetRouting.Core.Chain;

public interface ITailscaleVersionProbe
{
    /// <summary>Parsed "tailscaled --version"; null — the binary did not run or the output did not parse.</summary>
    Version? GetVersion();
}

/// <summary>
/// Reads the daemon version by running "tailscaled --version" (a local process,
/// a fraction of a second). Used by the auto elevation mode: tailscale 1.104.1+ listens
/// on its socket pipe without administrator rights, older builds do not.
/// </summary>
public sealed partial class TailscaleVersionProbe(string tailscaledPath) : ITailscaleVersionProbe
{
    public Version? GetVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tailscaledPath, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }) ?? throw new IOException($"failed to start {tailscaledPath}");
            var line = process.StandardOutput.ReadLine();
            process.WaitForExit(5000);
            return ParseVersion(line);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"1.104.1 tailscale commit: ..." → 1.104.1; unparsable — null.</summary>
    internal static Version? ParseVersion(string? line)
    {
        var match = HeadVersionRegex().Match((line ?? "").Trim());
        return match.Success
            && Version.TryParse($"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}", out var version)
            ? version
            : null;
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex HeadVersionRegex();
}
