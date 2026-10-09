using System.Text.Json;

namespace PrivateNetRouting.Core.AppUpdate;

/// <summary>
/// GitHub releases of the application's own repository over api.github.com (the repository is
/// public, the request is anonymous). A 404 means nothing published; other non-success codes
/// and network failures throw — the caller decides how visible that is.
/// </summary>
public sealed class GitHubAppReleaseFeed : IAppReleaseFeed
{
    private static readonly string ApiUrl =
        $"https://api.github.com/repos/{AppUpdateChecker.RepoSlug}/releases/latest";

    private readonly HttpClient _http;

    public GitHubAppReleaseFeed()
        : this(new HttpClientHandler())
    {
    }

    internal GitHubAppReleaseFeed(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrivateNetRouting");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<(string Tag, string Url)?> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ApiUrl, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseRelease(body);
    }

    /// <summary>tag_name + html_url from the release JSON; anything missing or malformed → null.</summary>
    internal static (string Tag, string Url)? ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var url = root.GetProperty("html_url").GetString();
            return string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url) ? null : (tag, url);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
