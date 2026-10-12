using System.Net;
using System.Net.Http;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Fragment download: assembly from Range requests, resume from completed parts,
/// fragment retries, server refusal, cancellation with parts kept.
/// </summary>
public class FragmentDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pnr-dl-{Guid.NewGuid():N}");
    private readonly FileLog _log;
    private readonly HttpClient _http;
    private readonly RecordingHandler _handler;
    private readonly FragmentDownloader _downloader;

    public FragmentDownloaderTests()
    {
        Directory.CreateDirectory(_root);
        _log = new FileLog(Path.Combine(_root, "dl.log"));
        _handler = new RecordingHandler();
        _http = new HttpClient(_handler);
        _downloader = new FragmentDownloader(_http, new ClaudeUpdateConfig
        {
            ChunkBytes = 10,
            ParallelChunks = 6,
            ChunkRetries = 2,
            RetryDelaySeconds = 0,
            ChunkTimeoutSeconds = 5,
        }, _log);
    }

    [Fact]
    public async Task HeadReturnsContentLength()
    {
        var length = await _downloader.GetLengthAsync("https://pkg.example.com/app.msix", CancellationToken.None);

        Assert.Equal(RecordingHandler.ContentLength, length);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("HEAD", request.Method);
    }

    [Fact]
    public async Task DownloadAssemblesFileFromRanges()
    {
        var target = Path.Combine(_root, "app.msix");

        await _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None);

        Assert.Equal(RecordingHandler.Payload, await File.ReadAllBytesAsync(target));
        Assert.False(Directory.Exists(target + ".parts"));
        // each fragment is requested with exactly its own range
        Assert.Equal(10, _handler.Requests.Count(r => r.Method == "GET"));
        Assert.All(_handler.Requests, r => Assert.True(r.From is not null && r.To is not null && r.To - r.From + 1 == 10));
    }

    [Fact]
    public async Task DownloadResumesExistingParts()
    {
        var target = Path.Combine(_root, "app.msix");
        var partsDir = target + ".parts";
        Directory.CreateDirectory(partsDir);
        await File.WriteAllBytesAsync(Path.Combine(partsDir, "part0000"), RecordingHandler.Payload[0..10]);
        await File.WriteAllBytesAsync(Path.Combine(partsDir, "part0003"), RecordingHandler.Payload[30..40]);

        await _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None);

        Assert.Equal(RecordingHandler.Payload, await File.ReadAllBytesAsync(target));
        var ranges = _handler.Requests
            .Where(r => r.Method == "GET")
            .Select(r => r.From!.Value)
            .OrderBy(from => from)
            .ToList();
        Assert.Equal([10L, 20L, 40L, 50L, 60L, 70L, 80L, 90L], ranges); // fragments 0 and 3 are already complete
    }

    [Fact]
    public async Task DownloadSkipsWhenTargetFileIsComplete()
    {
        var target = Path.Combine(_root, "app.msix");
        await File.WriteAllBytesAsync(target, RecordingHandler.Payload);

        await _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.Payload.Length, null, CancellationToken.None);

        Assert.DoesNotContain(_handler.Requests, r => r.Method == "GET");
    }

    [Fact]
    public async Task FailingChunkIsRetriedAndThenSucceeds()
    {
        _handler.FailFirstAttempts[(20L, 29L)] = 2; // the first two attempts of the fragment return 500
        var target = Path.Combine(_root, "app.msix");

        await _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None);

        Assert.Equal(RecordingHandler.Payload, await File.ReadAllBytesAsync(target));
        var attempts = _handler.Requests.Count(r => r.Method == "GET" && r.From == 20);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExhaustedRetriesFailAndKeepParts()
    {
        _handler.FailFirstAttempts[(20L, 29L)] = 99;
        var target = Path.Combine(_root, "app.msix");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None));

        Assert.Contains("incomplete fragments", error.Message);
        Assert.True(Directory.Exists(target + ".parts")); // the resume data is kept
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task FullBodyInsteadOfRangeFailsFragment()
    {
        // the server ignores Range and returns 200 with the whole body, so the fragment cannot match in size
        _handler.FullBodyForRanges = true;
        var target = Path.Combine(_root, "app.msix");

        await Assert.ThrowsAsync<IOException>(() =>
            _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationKeepsDownloadedParts()
    {
        _handler.StallFrom = 50; // one fragment hangs until cancellation
        var target = Path.Combine(_root, "app.msix");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, cts.Token));

        Assert.False(File.Exists(target)); // the target is not assembled
        Assert.True(Directory.Exists(target + ".parts")); // parts are kept for resume
    }

    [Fact]
    public async Task ProgressReportsDoneBytes()
    {
        var reports = new List<(long Done, long Total)>();
        var progress = new SyncProgress<(long Done, long Total)>(reports.Add);
        var target = Path.Combine(_root, "app.msix");

        await _downloader.DownloadAsync(
            "https://pkg.example.com/app.msix",
            target,
            RecordingHandler.ContentLength,
            progress,
            CancellationToken.None);

        Assert.Contains(reports, r => r.Total == RecordingHandler.ContentLength);
        // the first report is before start (0), the last is the full size
        Assert.Equal(0, reports[0].Done);
        Assert.Equal(RecordingHandler.ContentLength, reports[^1].Done);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public void Dispose()
    {
        _http.Dispose();
        _log.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Fake server: serves payload slices by Range, can fail and hang.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public const int ContentLength = 100;

        public static readonly byte[] Payload =
            Enumerable.Range(0, ContentLength).Select(i => (byte)(i * 7 % 251)).ToArray();

        public List<(string Method, long? From, long? To)> Requests { get; } = [];

        /// <summary>How many first attempts of a range to reject (key is from/to).</summary>
        public Dictionary<(long From, long To), int> FailFirstAttempts { get; } = [];

        /// <summary>Answer 200 with the whole body to Range requests (server ignores Range).</summary>
        public bool FullBodyForRanges { get; set; }

        /// <summary>The range starting at this offset hangs until cancellation.</summary>
        public long? StallFrom { get; set; }

        private readonly Dictionary<(long From, long To), int> _attempts = [];
        private readonly object _gate = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var stall = false;
            HttpResponseMessage response;
            lock (_gate)
            {
                var range = request.Headers.Range?.Ranges.FirstOrDefault();
                Requests.Add((request.Method.Method, range?.From, range?.To));
                if (request.Method == HttpMethod.Head)
                {
                    return Response(HttpStatusCode.OK, Array.Empty<byte>(), ContentLength);
                }

                var from = range!.From!.Value;
                var to = range.To!.Value;
                stall = StallFrom == from;
                var key = (from, to);
                _attempts.TryGetValue(key, out var attempt);
                _attempts[key] = attempt + 1;
                if (FullBodyForRanges)
                {
                    response = Response(HttpStatusCode.OK, Payload);
                }
                else if (FailFirstAttempts.TryGetValue(key, out var failFirst) && attempt < failFirst)
                {
                    response = Response(HttpStatusCode.InternalServerError, "boom"u8.ToArray());
                }
                else
                {
                    response = Response(HttpStatusCode.PartialContent, Payload[(int)from..(int)(to + 1)]);
                }
            }

            if (stall)
            {
                await Task.Delay(Timeout.Infinite, ct); // simulate a hung fragment, until cancellation
            }

            return response;
        }

        private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, long? length = null)
        {
            var content = new ByteArrayContent(body);
            if (length is not null)
            {
                content.Headers.ContentLength = length;
            }

            return new HttpResponseMessage(status) { Content = content };
        }
    }
}
