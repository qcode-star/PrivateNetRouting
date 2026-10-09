using System.Text.Json;
using System.Text.RegularExpressions;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Updater;

public interface IUpdateUrlFinder
{
    /// <summary>Latest package URL; null — this source has no fresh address. The installed
    /// version lets the source tailor the request (the feed takes it as a parameter).</summary>
    Task<string?> FindLatestPackageUrlAsync(string? installedVersion, CancellationToken ct);
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

    public async Task<string?> FindLatestPackageUrlAsync(string? installedVersion, CancellationToken ct)
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

/// <summary>
/// Latest package URL from Claude's own update feed — the same endpoint the built-in updater
/// polls ({updater.updateFeedUrl}?device_id=…&version=…&os_version=…): a JSON manifest with
/// the current release and the .msix address, so the address is known at once, without
/// waiting for the built-in updater to leave it in the AppX journal. The feed answers only
/// through the chain — the caller asks it when the proxy is already alive. Any failure is a
/// warning and null: the journal lookup takes over, the check phase must not fail because of
/// the feed.
/// </summary>
public sealed class UpdateFeedUrlFinder(HttpClient http, UpdaterConfig config, FileLog log) : IUpdateUrlFinder
{
    /// <summary>Opaque client token the endpoint requires (device_id); carries nothing machine-specific.</summary>
    private const string FeedDeviceId = "pnr-update-check";

    public async Task<string?> FindLatestPackageUrlAsync(string? installedVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.UpdateFeedUrl))
        {
            return null;
        }

        var query =
            "device_id=" + Uri.EscapeDataString(FeedDeviceId) +
            "&version=" + Uri.EscapeDataString(installedVersion ?? "") +
            "&os_version=" + Uri.EscapeDataString(Environment.OSVersion.VersionString);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.FeedTimeoutSeconds)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.UpdateFeedUrl + "?" + query);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.Warn($"updater: the update feed responded {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var url = ExtractLatestUrl(json.RootElement, config.PackageHost);
            if (url is null)
            {
                log.Warn("updater: the update feed response had no recognizable package URL");
            }

            return url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) // timeout, transport, bad JSON — the feed must not fail the check
        {
            log.Warn($"updater: the update feed check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The package URL of the release the feed points to (currentRelease, or the first
    /// recognizable one): {currentRelease, releases: [{version, updateTo: {url}}]}. The URL
    /// must be https, point to .msix and stay on the package host. Null — the shape is not
    /// recognized (public static for tests).
    /// </summary>
    public static string? ExtractLatestUrl(JsonElement root, string packageHost)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("releases", out var releases)
            || releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var currentRelease = root.TryGetProperty("currentRelease", out var current)
            && current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : null;
        string? fallback = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || !release.TryGetProperty("updateTo", out var updateTo)
                || updateTo.ValueKind != JsonValueKind.Object
                || !updateTo.TryGetProperty("url", out var url)
                || url.ValueKind != JsonValueKind.String
                || url.GetString() is not { } candidate
                || !PackageUrl.IsValidPackageUrl(candidate)
                || !candidate.Contains(packageHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = release.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            if (version is not null && string.Equals(version, currentRelease, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            fallback ??= candidate;
        }

        return fallback;
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
