using System.Net;
using PrivateNetRouting.Core.AppUpdate;

namespace PrivateNetRouting.Tests;

public class GitHubAppReleaseFeedTests
{
    private const string ReleaseJson =
        """{"tag_name":"v0.2.0","html_url":"https://example.com/releases/v0.2.0"}""";

    [Fact]
    public async Task SuccessBodyIsParsed()
    {
        var feed = new GitHubAppReleaseFeed(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson),
        }));

        var release = await feed.GetLatestReleaseAsync(CancellationToken.None);

        Assert.Equal(("v0.2.0", "https://example.com/releases/v0.2.0"), release);
    }

    [Fact]
    public async Task NotFoundMeansNothingPublished()
    {
        var feed = new GitHubAppReleaseFeed(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var release = await feed.GetLatestReleaseAsync(CancellationToken.None);

        Assert.Null(release);
    }

    [Fact]
    public async Task ServerErrorThrows()
    {
        var feed = new GitHubAppReleaseFeed(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => feed.GetLatestReleaseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedBodyMeansNothingPublished()
    {
        var feed = new GitHubAppReleaseFeed(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>"),
        }));

        var release = await feed.GetLatestReleaseAsync(CancellationToken.None);

        Assert.Null(release);
    }

    [Fact]
    public async Task MissingFieldsMeansNothingPublished()
    {
        var feed = new GitHubAppReleaseFeed(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        }));

        var release = await feed.GetLatestReleaseAsync(CancellationToken.None);

        Assert.Null(release);
    }

    /// <summary>HTTP stub: the same canned response for every request, calls counted.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
