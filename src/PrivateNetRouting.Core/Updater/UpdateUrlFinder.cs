using System.Text.RegularExpressions;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Updater;

public interface IUpdateUrlFinder
{
    /// <summary>Latest package URL from the AppX journal; null — no fresh URL.</summary>
    Task<string?> FindLatestPackageUrlAsync(CancellationToken ct);
}

/// <summary>
/// URL of the latest Claude package from the AppX journal: the built-in updater, having failed to download,
/// leaves it in Microsoft-Windows-AppXDeploymentServer/Operational about once an hour
/// (the first events of the journal are the freshest). The whole message is fetched via Get-WinEvent,
/// the URL is parsed by <see cref="PackageUrl.ExtractFromMessage"/>.
/// </summary>
public sealed class PowerShellJournalUrlFinder(UpdaterConfig config, IPowerShellExecutor powershell, FileLog log) : IUpdateUrlFinder
{
    private const string LogName = "Microsoft-Windows-AppXDeploymentServer/Operational";

    public async Task<string?> FindLatestPackageUrlAsync(CancellationToken ct)
    {
        var script =
            "$ev = Get-WinEvent -LogName '" + LogName + "' -MaxEvents " + config.JournalMaxEvents +
            " -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Message -like '*" + config.PackageHost.Replace("'", "''") + "*' } | " +
            "Select-Object -First 1; " +
            "if ($ev) { $ev.Message }";
        var (code, output) = await powershell.RunScriptAsync(script, ct);
        if (code != 0)
        {
            log.Warn($"updater: Get-WinEvent failed (exit {code}): {output}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var url = PackageUrl.ExtractFromMessage(output, config.PackageHost);
        if (url is null)
        {
            log.Warn("updater: a journal event matched the package host but contained no .msix URL");
        }

        return url;
    }
}

/// <summary>Parsing of package URLs: extraction from journal events, version and file name from the URL.</summary>
public static partial class PackageUrl
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Extracts the package URL from the event message. The URL in the message may be broken
    /// by a line break — so whitespace is collapsed before the search.
    /// </summary>
    public static string? ExtractFromMessage(string message, string host)
    {
        if (!message.Contains(host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var flat = Whitespace().Replace(message, "");
        var match = Regex.Match(flat, $"https://{Regex.Escape(host)}/[^\"']+\\.msix", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    /// <summary>Version from the package path (/releases/win32/x64/&lt;version&gt;/...); null — a path of another shape.</summary>
    public static string? ParseOfferedVersion(string url)
    {
        var match = Regex.Match(url, @"/releases/win32/x64/([^/]+)/", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Package file name without the query string; empty if the URL does not point to .msix.</summary>
    public static string ParseFileName(string url)
    {
        var name = Path.GetFileName(url.Split('?')[0]);
        return name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ? name : "";
    }

    /// <summary>A manual URL is valid: https and points to .msix.</summary>
    public static bool IsValidPackageUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && ParseFileName(url).Length > 0;
}
