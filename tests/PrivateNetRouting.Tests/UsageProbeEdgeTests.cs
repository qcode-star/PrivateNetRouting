using System.Net;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Tests;

/// <summary>Edge cases of the idle stopper's usage probe: foreign Codex-named processes, external proxy clients, a marker from the future.</summary>
public class UsageProbeEdgeTests
{
    private const string CodexMarker = @"\WindowsApps\OpenAI.Codex_";

    [Fact]
    public void ForeignProcessWithTheCodexImageNameDoesNotKeepTheChain()
    {
        var finder = new NamedFinder(
            new FoundProcess(10, @"""C:\Program Files\WindowsApps\Other.ChatGPT_1.0_x64\app\ChatGPT.exe"""));
        var probe = new ClaudeUsageProbe(
            finder,
            new Counter(0, 0),
            proxyPort: 18081,
            activityMarkerPath: null,
            codexProcessName: "ChatGPT",
            codexCommandLineMarker: CodexMarker);

        Assert.Null(probe.GetInUseReason(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void CodexProcessFromItsPackageKeepsTheChain()
    {
        var finder = new NamedFinder(
            new FoundProcess(11, @"""C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64\app\ChatGPT.exe"""));
        var probe = new ClaudeUsageProbe(
            finder,
            new Counter(0, 0),
            proxyPort: 18081,
            activityMarkerPath: null,
            codexProcessName: "ChatGPT",
            codexCommandLineMarker: CodexMarker);

        Assert.Equal("codex processes: 1", probe.GetInUseReason(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void ExternalProxyClientsAreCountedByTheRemoteEndpoint()
    {
        var upstream = IPAddress.Parse("192.0.2.10");
        var probe = new ClaudeUsageProbe(
            new NamedFinder(),
            new Counter(localPortMatches: 7, remoteMatches: 0),
            proxyPort: 1080,
            activityMarkerPath: null,
            externalProxyAddress: upstream);

        // unrelated traffic to the same port number elsewhere must not count as a client
        Assert.Null(probe.GetInUseReason(TimeSpan.FromMinutes(10)));

        probe = new ClaudeUsageProbe(
            new NamedFinder(),
            new Counter(localPortMatches: 7, remoteMatches: 2),
            proxyPort: 1080,
            activityMarkerPath: null,
            externalProxyAddress: upstream);
        Assert.Equal("established connections to 1080: 2", probe.GetInUseReason(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void MarkerStampedFarInTheFutureIsNotActivity()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"pnr-marker-{Guid.NewGuid():N}");
        File.WriteAllText(marker, "x");
        try
        {
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow + TimeSpan.FromHours(3)); // the clock was stepped back
            var probe = new ClaudeUsageProbe(new NamedFinder(), new Counter(0, 0), 18081, marker);
            Assert.Null(probe.GetInUseReason(TimeSpan.FromMinutes(10)));

            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow + TimeSpan.FromSeconds(20)); // a small skew still counts
            Assert.StartsWith("proxy activity", probe.GetInUseReason(TimeSpan.FromMinutes(10)));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    private sealed class NamedFinder(params FoundProcess[] named) : IProcessFinder
    {
        public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) => [];

        public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) =>
            namePart.Equals("claude", StringComparison.OrdinalIgnoreCase) ? [] : named;

        public FoundProcess? FindById(int processId) => null;
    }

    private sealed class Counter(int localPortMatches, int remoteMatches) : IEstablishedConnectionCounter
    {
        public int CountOnLocalPort(int port) => localPortMatches;

        public int CountToRemote(IPAddress address, int port) => remoteMatches;
    }
}
