using PrivateNetRouting.Core.AppUpdate;

namespace PrivateNetRouting.Tests;

public class AppUpdateTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("V1.10.3", "1.10.3")]
    [InlineData("v1.2", "1.2")]
    public void TagVersionIsParsedWithOptionalV(string tag, string expected) =>
        Assert.Equal(new Version(expected), AppUpdateChecker.ParseTagVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    public void UnparsableTagsAreIgnored(string? tag) =>
        Assert.Null(AppUpdateChecker.ParseTagVersion(tag));

    [Fact]
    public async Task NewerReleaseMeansUpdate()
    {
        var checker = new AppUpdateChecker(
            new Version(0, 1, 0),
            new FakeAppReleaseFeed(("v0.2.0", "https://example.com/releases/v0.2.0")));

        var check = await checker.CheckAsync(CancellationToken.None);

        Assert.True(check.HasUpdate);
        Assert.Equal(new Version(0, 2, 0), check.Latest);
        Assert.Equal("https://example.com/releases/v0.2.0", check.ReleaseUrl);
    }

    [Fact]
    public async Task SameVersionMeansNoUpdate()
    {
        var checker = new AppUpdateChecker(
            new Version(0, 2, 0),
            new FakeAppReleaseFeed(("v0.2.0", "https://example.com/releases/v0.2.0")));

        var check = await checker.CheckAsync(CancellationToken.None);

        Assert.False(check.HasUpdate);
        Assert.Equal(new Version(0, 2, 0), check.Latest);
    }

    [Fact]
    public async Task UnparsableLatestTagMeansNoUpdate()
    {
        var checker = new AppUpdateChecker(
            new Version(0, 2, 0),
            new FakeAppReleaseFeed(("snapshot", "https://example.com/releases/snapshot")));

        var check = await checker.CheckAsync(CancellationToken.None);

        Assert.False(check.HasUpdate);
        Assert.Null(check.Latest);
        Assert.Null(check.ReleaseUrl);
    }

    [Fact]
    public async Task EmptyFeedMeansNoUpdate()
    {
        var checker = new AppUpdateChecker(new Version(0, 2, 0), new FakeAppReleaseFeed(null));

        var check = await checker.CheckAsync(CancellationToken.None);

        Assert.False(check.HasUpdate);
        Assert.Null(check.Latest);
    }

    [Fact]
    public async Task FeedFailurePropagates()
    {
        var checker = new AppUpdateChecker(
            new Version(0, 2, 0),
            new FakeAppReleaseFeed(null, new HttpRequestException("down")));

        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckAsync(CancellationToken.None));
    }
}
