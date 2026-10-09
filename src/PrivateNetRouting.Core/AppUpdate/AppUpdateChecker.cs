using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.AppUpdate;

/// <summary>
/// The result of an app self-update check: the running version against the latest published
/// release. <see cref="Latest"/> is null when the feed answered nothing usable (nothing
/// published, malformed body) — that means "no update", not an error.
/// </summary>
public sealed record AppUpdateCheck(Version Current, Version? Latest, string? ReleaseUrl)
{
    /// <summary>True when the feed reports a release newer than the running build.</summary>
    public bool HasUpdate => Latest is not null && Latest > Current;
}

/// <summary>Source of the latest published release of the application itself.</summary>
public interface IAppReleaseFeed
{
    /// <summary>
    /// The latest stable release: its tag (expected "vX.Y.Z") and the human URL; null when
    /// nothing was published. Network failures throw — the caller decides how visible that is.
    /// </summary>
    Task<(string Tag, string Url)?> GetLatestReleaseAsync(CancellationToken ct);
}

/// <summary>
/// App self-update: the running version against the application's own GitHub releases (the
/// help-menu item and the one-time start check). Versions compare numerically per component;
/// a feed tag that is not a version is ignored.
/// </summary>
public sealed class AppUpdateChecker
{
    /// <summary>The application's own repository: the releases feed source and the page to open.</summary>
    public const string RepoSlug = "qcode-star/PrivateNetRouting";

    public const string ReleasesPageUrl = $"https://github.com/{RepoSlug}/releases/latest";

    private readonly Version _current;
    private readonly IAppReleaseFeed _feed;

    public AppUpdateChecker(Version current, IAppReleaseFeed feed, FileLog? log = null)
    {
        _current = current;
        _feed = feed;
        Log = log;
    }

    /// <summary>The module log (appupdate.log); null in tests and when the caller passed none.</summary>
    public FileLog? Log { get; }

    /// <summary>
    /// One check; failures propagate to the caller — a menu item shows them, the start check
    /// logs them quietly.
    /// </summary>
    public async Task<AppUpdateCheck> CheckAsync(CancellationToken ct)
    {
        var release = await _feed.GetLatestReleaseAsync(ct).ConfigureAwait(false);
        var latest = release is null ? null : ParseTagVersion(release.Value.Tag);
        Log?.Info($"self-update: running {_current}, latest {latest?.ToString() ?? "unknown"}");
        return new AppUpdateCheck(_current, latest, latest is null ? null : release!.Value.Url);
    }

    /// <summary>"v0.2.0"/"0.2.0" → 0.2.0; anything unparsable → null.</summary>
    public static Version? ParseTagVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }
}
