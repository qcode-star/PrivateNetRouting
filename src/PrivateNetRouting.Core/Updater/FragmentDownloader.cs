using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Updater;

/// <summary>Downloading the package via the proxy chain.</summary>
public interface IMsixDownloader
{
    /// <summary>HEAD request: package size (Content-Length).</summary>
    Task<long> GetLengthAsync(string url, CancellationToken ct);

    /// <summary>
    /// Chunked download: parts of updater.chunkBytes across updater.parallelChunks threads,
    /// a finished part is a file of the expected size (the download resumes from where it stopped after any
    /// failure). The progress counts the bytes received so far, including the parts still in flight —
    /// it moves as the data arrives, not in jumps of a whole part. On cancellation throws
    /// OperationCanceledException; incomplete parts remain on disk.
    /// </summary>
    Task DownloadAsync(string url, string targetFile, long expectedLength, IProgress<(long DoneBytes, long TotalBytes)>? progress, CancellationToken ct);
}

/// <summary>
/// Multithreaded chunked download: a single thread via the
/// chain hits the DoH+sidecar latency, several threads yield a few MB/s. A fragment with
/// retries, then a size check and concatenation.
/// </summary>
public sealed class FragmentDownloader(HttpClient http, ClaudeUpdateConfig config, FileLog log, TimeSpan? progressInterval = null, TimeSpan? downloadLockWait = null) : IMsixDownloader
{
    /// <summary>How often an in-flight part reports its bytes; without a limit the reports would flood the UI.</summary>
    private readonly TimeSpan _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(250);

    /// <summary>How long a second downloader of the same target waits for the per-target lock.</summary>
    private readonly TimeSpan _downloadLockWait = downloadLockWait ?? TimeSpan.FromSeconds(15);

    private const int CopyBufferSize = 81920;

    private sealed class Chunk(long start, long end, string file)
    {
        public long Start { get; } = start;
        public long End { get; } = end;
        public string File { get; } = file;
        public long Size => End - Start + 1;

        private long _written;

        /// <summary>Bytes of the current attempt received so far; zeroed when the attempt restarts the part.</summary>
        public long Written => Interlocked.Read(ref _written);

        public void AddWritten(long delta) => Interlocked.Add(ref _written, delta);

        public void ResetWritten() => Interlocked.Exchange(ref _written, 0);
    }

    public async Task<long> GetLengthAsync(string url, CancellationToken ct)
    {
        // the HttpClient has no global timeout (long fragments): a stalled egress would hold the
        // size query forever, so it gets the per-request limit
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.HeadTimeoutSeconds)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"HEAD failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return response.Content.Headers.ContentLength ?? 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"HEAD timed out after {config.HeadTimeoutSeconds}s — the egress accepts connections but does not answer");
        }
    }

    public async Task DownloadAsync(string url, string targetFile, long expectedLength, IProgress<(long, long)>? progress, CancellationToken ct)
    {
        // the GUI and `pnr update` must not feed one .parts directory at once
        using var downloadLock = await AcquireDownloadLockAsync(targetFile, _downloadLockWait, ct).ConfigureAwait(false);
        try
        {
            await DownloadLockedAsync(url, targetFile, expectedLength, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            downloadLock.Dispose();
            try
            {
                File.Delete(targetFile + ".lock");
            }
            catch (IOException)
            {
                // the next acquirer has the lock open already — the stale name is harmless
            }
        }
    }

    private async Task DownloadLockedAsync(string url, string targetFile, long expectedLength, IProgress<(long, long)>? progress, CancellationToken ct)
    {
        if (expectedLength <= 0)
        {
            throw new IOException("the server did not report the package size (Content-Length)");
        }

        if (File.Exists(targetFile) && new FileInfo(targetFile).Length == expectedLength)
        {
            log.Info("updater: the package is already downloaded in full");
            progress?.Report((expectedLength, expectedLength));
            return;
        }

        var partsDir = targetFile + ".parts";
        Directory.CreateDirectory(partsDir);
        var chunks = new List<Chunk>();
        for (long start = 0, n = 0; start < expectedLength; start += config.ChunkBytes, n++)
        {
            var end = Math.Min(start + config.ChunkBytes - 1, expectedLength - 1);
            chunks.Add(new Chunk(start, end, Path.Combine(partsDir, $"part{n:0000}")));
        }

        var pending = chunks.Where(chunk => !IsComplete(chunk)).ToList();
        var doneBytes = expectedLength - pending.Sum(chunk => chunk.Size);
        log.Info($"updater: {chunks.Count} fragments of {ByteSize.Format(config.ChunkBytes)}, to download: {pending.Count}, parallel: {config.ParallelChunks}");

        // the done bytes include the parts still in flight: the bar moves as the data arrives,
        // not in jumps of a whole fragment
        long Done() => doneBytes + pending.Sum(chunk => chunk.Written);
        void Report() => progress?.Report((Done(), expectedLength));
        Report();

        if (pending.Count > 0)
        {
            var next = 0;
            var gate = new object();
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(config.ParallelChunks, pending.Count)))
                .Select(_ => Task.Run(WorkerAsync))
                .ToArray();

            async Task WorkerAsync()
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    Chunk chunk;
                    lock (gate)
                    {
                        if (next >= pending.Count)
                        {
                            return;
                        }

                        chunk = pending[next++];
                    }

                    try
                    {
                        await DownloadChunkAsync(chunk, url, Report, ct);
                        log.Debug($"updater: fragment {Path.GetFileName(chunk.File)} done ({ByteSize.Format(chunk.Size)}, total {ByteSize.Format(Done())})");
                        Report();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // fragment did not arrive after all retries: the rest continue, the result is checked below
                        log.Warn($"updater: fragment {Path.GetFileName(chunk.File)} failed permanently: {ex.Message}");
                    }
                }
            }

            await Task.WhenAll(workers);

            var bad = pending.Where(chunk => !IsComplete(chunk)).ToList();
            if (bad.Count > 0)
            {
                throw new IOException($"incomplete fragments: {bad.Count} of {chunks.Count} — resume keeps what is downloaded");
            }
        }

        Concatenate(chunks, targetFile, expectedLength);
        try
        {
            Directory.Delete(partsDir, recursive: true);
        }
        catch (IOException)
        {
            // an empty fragments directory in the cache does not hurt
        }

        log.Info($"updater: package assembled: {targetFile} ({ByteSize.Format(expectedLength)})");
    }

    /// <summary>
    /// A per-target lock file (held open with FileShare.None) serialises concurrent downloads —
    /// the GUI and `pnr update` share the cache path. A named mutex would not do here: its
    /// ownership is bound to the acquiring thread, and the pool can retire that thread while the
    /// async download still runs — the next acquirer is told "abandoned" and walks in. A lock
    /// file has no thread affinity, and a dead holder's handle the OS closes itself.
    /// </summary>
    private static async Task<FileStream> AcquireDownloadLockAsync(string targetFile, TimeSpan wait, CancellationToken ct)
    {
        var path = targetFile + ".lock";
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return File.Open(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new IOException($"another download into {targetFile} is holding the lock (waited {wait.TotalSeconds}s)");
            }
        }
    }

    private async Task DownloadChunkAsync(Chunk chunk, string url, Action report, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            chunk.ResetWritten();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(config.ChunkTimeoutSeconds));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.End);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new IOException($"range request returned {(int)response.StatusCode} instead of 206");
                }

                // a 206 for a DIFFERENT range than requested would land in the wrong part of the
                // package: the answer must name exactly the bytes that were asked for
                var served = response.Content.Headers.ContentRange;
                if (served is not { HasRange: true } || served.From != chunk.Start || served.To != chunk.End)
                {
                    throw new IOException($"range response ({served}) does not match the requested {chunk.Start}-{chunk.End}");
                }

                using var output = File.Create(chunk.File);
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[CopyBufferSize];
                long written = 0;
                var nextReport = Environment.TickCount64;
                while (written < chunk.Size)
                {
                    var read = await body.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    // the range was checked, but a broken server may still send more bytes than it
                    // named: read no further than the fragment — an oversized part would never
                    // pass the size check anyway
                    var take = (int)Math.Min(read, chunk.Size - written);
                    await output.WriteAsync(buffer.AsMemory(0, take), timeout.Token);
                    written += take;
                    chunk.AddWritten(take);
                    if (Environment.TickCount64 >= nextReport)
                    {
                        nextReport = Environment.TickCount64 + (long)_progressInterval.TotalMilliseconds;
                        report();
                    }
                }

                await output.FlushAsync(timeout.Token); // the length is checked on disk — the FileStream buffer must be flushed
                if (written != chunk.Size)
                {
                    throw new IOException($"fragment size mismatch: expected {chunk.Size}, got {written}");
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                chunk.ResetWritten(); // the part restarts from scratch — its partial bytes leave the total
                if (attempt >= config.ChunkRetries)
                {
                    throw;
                }

                log.Warn($"updater: fragment {Path.GetFileName(chunk.File)} attempt {attempt + 1} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(config.RetryDelaySeconds), ct);
            }
        }
    }

    private static bool IsComplete(Chunk chunk) =>
        File.Exists(chunk.File) && new FileInfo(chunk.File).Length == chunk.Size;

    private static void Concatenate(IReadOnlyList<Chunk> chunks, string targetFile, long expectedLength)
    {
        using var output = File.Create(targetFile);
        foreach (var chunk in chunks)
        {
            using var input = File.OpenRead(chunk.File);
            input.CopyTo(output);
        }

        if (output.Length != expectedLength)
        {
            throw new IOException($"assembled size {output.Length} != expected {expectedLength}");
        }
    }
}

/// <summary>Sizes for logs and UI (invariant culture).</summary>
public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.##} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.#} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };
}
