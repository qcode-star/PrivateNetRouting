using System.Net;
using System.Net.Http;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Fragment download: assembly from Range requests, resume from completed parts,
/// fragment retries, server refusal, cancellation with parts kept, in-flight progress.
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
    public async Task HeadTimesOutOnAStalledEgress()
    {
        using var http = new HttpClient(new StallingHandler());
        // the HEAD has its own limit: the long per-chunk one does not hold it
        var downloader = new FragmentDownloader(http, new ClaudeUpdateConfig { HeadTimeoutSeconds = 1, ChunkTimeoutSeconds = 1800 }, _log);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(
            () => downloader.GetLengthAsync("https://pkg.example.com/app.msix", CancellationToken.None));

        Assert.Contains("timed out after 1s", error.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HeadCancellationByTheCallerIsNotATimeout()
    {
        using var http = new HttpClient(new StallingHandler());
        var downloader = new FragmentDownloader(http, new ClaudeUpdateConfig { HeadTimeoutSeconds = 30 }, _log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.GetLengthAsync("https://pkg.example.com/app.msix", cts.Token));
    }

    [Fact]
    public async Task ManifestTimeoutMeansNoVersionButCallerCancellationPropagates()
    {
        using var http = new HttpClient(new StallingHandler());
        var manifest = new CodexVersionManifest(
            http, new CodexUpdateConfig { UpdateVersionUrl = "https://manifest.example.com/v.json" }, _log,
            requestTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Null(await manifest.FindOfferedVersionAsync(CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var slow = new CodexVersionManifest(
            http, new CodexUpdateConfig { UpdateVersionUrl = "https://manifest.example.com/v.json" }, _log,
            requestTimeout: TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.FindOfferedVersionAsync(cts.Token));
    }

    /// <summary>An egress that accepts the connection and then never answers.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
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
        await File.WriteAllTextAsync(
            Path.Combine(partsDir, FragmentDownloader.ResumeManifestName),
            FragmentDownloader.ResumeManifest("https://pkg.example.com/app.msix", RecordingHandler.ContentLength, 10));
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

    /// <summary>
    /// Parts cut with another chunk size (updater.chunkBytes changed between the runs) have the
    /// expected sizes of other ranges: trusting their length would assemble a broken package. Parts
    /// of another manifest, or of none, are discarded and the download starts over.
    /// </summary>
    [Theory]
    [InlineData("chunk")]
    [InlineData("url")]
    [InlineData("none")]
    public async Task PartsOfAnotherCutAreDiscardedBeforeTheDownload(string mismatch)
    {
        var target = Path.Combine(_root, "app.msix");
        var partsDir = target + ".parts";
        Directory.CreateDirectory(partsDir);
        var manifest = mismatch switch
        {
            "chunk" => FragmentDownloader.ResumeManifest("https://pkg.example.com/app.msix", RecordingHandler.ContentLength, 20),
            "url" => FragmentDownloader.ResumeManifest("https://pkg.example.com/other.msix", RecordingHandler.ContentLength, 10),
            _ => null,
        };
        if (manifest is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(partsDir, FragmentDownloader.ResumeManifestName), manifest);
        }

        // a part of a 20-byte cut is 10 bytes too when it is the tail; here: bytes 20..29 as "part0001"
        await File.WriteAllBytesAsync(Path.Combine(partsDir, "part0001"), RecordingHandler.Payload[20..30]);

        await _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None);

        Assert.Equal(RecordingHandler.Payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(10, _handler.Requests.Count(r => r.Method == "GET")); // every fragment anew
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
    public async Task A206ForADifferentRangeIsRejected()
    {
        // a lying Content-Range would land the bytes in the wrong part of the package: every
        // fragment fails (the detail is in the log), the download reports incomplete fragments
        _handler.LieAboutRange = true;
        var target = Path.Combine(_root, "app.msix");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None));

        Assert.Contains("incomplete fragments", error.Message);
    }

    [Fact]
    public async Task ABiggerThanPromisedBodyIsCappedAtTheFragment()
    {
        _handler.Overshoot = true;
        var target = Path.Combine(_root, "app.msix");

        await _downloader.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None);

        Assert.Equal(RecordingHandler.Payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task ASecondDownloadOfTheSameTargetIsRefusedWhileOneRuns()
    {
        _handler.StallFrom = 50;
        var target = Path.Combine(_root, "app.msix");
        var slow = new FragmentDownloader(_http, new ClaudeUpdateConfig
        {
            ChunkBytes = 10,
            ParallelChunks = 1,
            ChunkRetries = 0,
            RetryDelaySeconds = 0,
            ChunkTimeoutSeconds = 30,
        }, _log);
        var impatient = new FragmentDownloader(_http, new ClaudeUpdateConfig
        {
            ChunkBytes = 10,
            ParallelChunks = 1,
            ChunkRetries = 0,
            RetryDelaySeconds = 0,
            ChunkTimeoutSeconds = 30,
        }, _log, downloadLockWait: TimeSpan.FromMilliseconds(200));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var first = slow.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, cts.Token);
        // the second may start only when the first demonstrably holds the lock (it is stalled
        // inside its fragment): a wall-clock delay would race the parallel test load
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!_handler.Requests.Any(r => r.Method == "GET" && r.From == 50))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the first download never reached its stalled fragment");
            }

            await Task.Delay(20);
        }

        var second = impatient.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Contains("holding the lock", error.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); // it still ends cancelled

        // the lock frees on the holder's exit: the next download of the same target proceeds
        _handler.StallFrom = null;
        await slow.DownloadAsync("https://pkg.example.com/app.msix", target, RecordingHandler.ContentLength, null, CancellationToken.None);
        Assert.Equal(RecordingHandler.Payload, await File.ReadAllBytesAsync(target));
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

    [Fact]
    public async Task ProgressCountsBytesInFlight()
    {
        // a body trickling in two bytes at a time: the progress must move between the fragment
        // boundaries (multiples of the fragment size), not jump over them
        _handler.PieceSize = 2;
        var downloader = new FragmentDownloader(_http, new ClaudeUpdateConfig
        {
            ChunkBytes = 10,
            ParallelChunks = 1,
            ChunkRetries = 2,
            RetryDelaySeconds = 0,
            ChunkTimeoutSeconds = 5,
        }, _log);
        var reports = new List<(long Done, long Total)>();
        var progress = new SyncProgress<(long Done, long Total)>(reports.Add);
        var target = Path.Combine(_root, "app.msix");

        await downloader.DownloadAsync(
            "https://pkg.example.com/app.msix",
            target,
            RecordingHandler.ContentLength,
            progress,
            CancellationToken.None);

        Assert.Equal(RecordingHandler.Payload, await File.ReadAllBytesAsync(target));
        Assert.Contains(reports, r => r.Done % 10 != 0);
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

        /// <summary>Answer 206 but name a shifted range in Content-Range (a lying server).</summary>
        public bool LieAboutRange { get; set; }

        /// <summary>Send more body bytes than the named range covers (a broken server).</summary>
        public bool Overshoot { get; set; }

        /// <summary>The range starting at this offset hangs until cancellation.</summary>
        public long? StallFrom { get; set; }

        /// <summary>When set, the body trickles out in pieces of this many bytes per read.</summary>
        public int? PieceSize { get; set; }

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
                    var body = Payload[(int)from..(int)(to + 1)];
                    if (Overshoot)
                    {
                        body = [.. body, 0xDE, 0xAD, 0xBE, 0xEF, 0xDE]; // more than the range names
                    }

                    response = Response(HttpStatusCode.PartialContent, body, piece: PieceSize);
                    response.Content.Headers.ContentRange = LieAboutRange
                        ? new System.Net.Http.Headers.ContentRangeHeaderValue(from + 5, to + 5)
                        : new System.Net.Http.Headers.ContentRangeHeaderValue(from, to);
                }
            }

            if (stall)
            {
                await Task.Delay(Timeout.Infinite, ct); // simulate a hung fragment, until cancellation
            }

            return response;
        }

        private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, long? length = null, int? piece = null)
        {
            HttpContent content = piece is null
                ? new ByteArrayContent(body)
                : new StreamContent(new PiecewiseStream(body, piece.Value));
            if (length is not null)
            {
                content.Headers.ContentLength = length;
            }

            return new HttpResponseMessage(status) { Content = content };
        }
    }

    /// <summary>A body that returns at most a few bytes per read — the progress must count them as they arrive.</summary>
    private sealed class PiecewiseStream(byte[] data, int piece) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Fill(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Fill(buffer.Span));
        }

        private int Fill(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(piece, buffer.Length), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
