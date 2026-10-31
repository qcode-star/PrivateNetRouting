using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Tests;

public class LogStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-logs-").FullName;

    // ----- listing and tail -----

    [Fact]
    public void ListShowsCurrentAndRotatedLogsOnly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "chain.log"), "line\r\n");
        File.WriteAllText(Path.Combine(_tempDir, "chain.log.1"), "old\r\n");
        File.WriteAllText(Path.Combine(_tempDir, "chain.log.12"), "older\r\n");
        File.WriteAllText(Path.Combine(_tempDir, "notes.txt"), "junk");
        File.WriteAllText(Path.Combine(_tempDir, "chain.log.txt"), "junk");

        var names = new LogStore(_tempDir).List().Select(f => f.Name).ToList();

        Assert.Equal(["chain.log", "chain.log.1", "chain.log.12"], names);
    }

    [Fact]
    public void TailReturnsLastLinesByNameWithOrWithoutExtension()
    {
        var path = Path.Combine(_tempDir, "watchdog.log");
        File.WriteAllLines(path, Enumerable.Range(1, 100).Select(i => $"line-{i}"));

        var store = new LogStore(_tempDir);
        var byExact = store.ReadTail("watchdog.log", 10);
        var byShort = store.ReadTail("watchdog", 10);

        Assert.NotNull(byExact);
        Assert.Equal(Enumerable.Range(91, 10).Select(i => $"line-{i}"), byExact!.Lines);
        Assert.Equal(byExact.Lines, byShort!.Lines);
        Assert.Null(store.ReadTail("missing", 10));
    }

    // ----- external rotation -----

    [Fact]
    public void RotateShiftsAgeChainAndDropsBeyondKeep()
    {
        var store = new LogStore(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "proxy.log"), "current");
        File.WriteAllText(Path.Combine(_tempDir, "proxy.log.1"), "one");
        File.WriteAllText(Path.Combine(_tempDir, "proxy.log.2"), "two");

        var result = store.Rotate("proxy.log", keep: 2);

        Assert.True(result.Ok);
        Assert.Equal("current", File.ReadAllText(Path.Combine(_tempDir, "proxy.log.1"))); // the current one moved to .1
        Assert.Equal("one", File.ReadAllText(Path.Combine(_tempDir, "proxy.log.2"))); // the old .1 shifted to .2
        Assert.False(File.Exists(Path.Combine(_tempDir, "proxy.log")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "proxy.log.3"))); // the old .2 ("two") is pushed beyond keep
    }

    [Fact]
    public void RotateMissingFileIsReportedAsUnknown()
    {
        var result = new LogStore(_tempDir).Rotate("absent.log", keep: 2);
        Assert.False(result.Ok);
        Assert.Contains("no such log", result.Detail);
    }

    [Fact]
    public void TailOfALargeFileKeepsTheLastLinesWholeAndTheTextIntact()
    {
        var path = Path.Combine(_tempDir, "big.log");
        // 5000 lines of ~100 bytes with multi-byte characters: well past one read chunk
        File.WriteAllLines(path, Enumerable.Range(1, 5000).Select(i => $"line-{i} — {new string('я', 40)}"));

        var tail = new LogStore(_tempDir).ReadTail("big", 3000);

        Assert.NotNull(tail);
        Assert.Equal(3000, tail!.Lines.Count);
        Assert.Equal($"line-2001 — {new string('я', 40)}", tail.Lines[0]);
        Assert.Equal($"line-5000 — {new string('я', 40)}", tail.Lines[^1]);
        Assert.DoesNotContain(tail.Lines, line => line.Contains('\uFFFD'));
    }

    [Fact]
    public void RotateFileHeldOpenByAnotherProcessFails()
    {
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path, "held");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); // like a live FileLog

        var result = new LogStore(_tempDir).Rotate("app.log", keep: 2);

        Assert.False(result.Ok);
        Assert.Contains("locked", result.Detail);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);
        Assert.Equal("held", text.ReadToEnd()); // the file is untouched
    }

    [Fact]
    public void RotateOfAHeldFileLeavesTheAgeChainIntact()
    {
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path, "held");
        File.WriteAllText(path + ".1", "one");
        File.WriteAllText(path + ".2", "two");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = new LogStore(_tempDir).Rotate("app.log", keep: 3);

        Assert.False(result.Ok);
        Assert.Equal("one", File.ReadAllText(path + ".1"));
        Assert.Equal("two", File.ReadAllText(path + ".2"));
        Assert.False(File.Exists(path + ".3"));
    }

    [Fact]
    public void RotateFoldsTheStagingFilesOfACrashedRotationIntoTheAgeChain()
    {
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path + ".1", "one|");
        File.WriteAllText(path + ".rotating", "legacy|");
        File.SetLastWriteTimeUtc(path + ".rotating", DateTime.UtcNow.AddMinutes(-2));
        File.WriteAllText(path + $".rotating-{int.MaxValue}-dead", "dead|"); // no such process
        File.SetLastWriteTimeUtc(path + $".rotating-{int.MaxValue}-dead", DateTime.UtcNow.AddMinutes(-1));
        File.WriteAllText(path, "current|");

        var result = new LogStore(_tempDir).Rotate("app.log", keep: 3);

        Assert.True(result.Ok);
        Assert.Equal("current|", File.ReadAllText(path + ".1"));
        Assert.Equal("one|legacy|dead|", File.ReadAllText(path + ".2")); // oldest first, just behind .1
        Assert.Empty(Directory.GetFiles(_tempDir, "app.log.rotating*"));
    }

    [Fact]
    public void RotateLeavesTheStagingFileOfALiveRotationAlone()
    {
        var path = Path.Combine(_tempDir, "app.log");
        var live = path + ".rotating-4-inflight"; // pid 4: the System process, always alive
        File.WriteAllText(live, "in flight");
        File.WriteAllText(path, "current");

        Assert.True(new LogStore(_tempDir).Rotate("app.log", keep: 2).Ok);

        Assert.Equal("in flight", File.ReadAllText(live));
        Assert.Equal("current", File.ReadAllText(path + ".1"));
    }

    [Fact]
    public void AFailedShiftPutsTheCurrentLogBackWithoutLoss()
    {
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path, "current");
        File.WriteAllText(path + ".1", "one");
        File.WriteAllText(path + ".2", "two");

        LogRotationResult result;
        using (new FileStream(path + ".1", FileMode.Open, FileAccess.Read, FileShare.Read)) // .1 cannot move
        {
            result = new LogStore(_tempDir).Rotate("app.log", keep: 3);
        }

        Assert.False(result.Ok);
        Assert.Equal("current", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(path + ".1"));
        Assert.Empty(Directory.GetFiles(_tempDir, "app.log.rotating*"));
    }

    [Fact]
    public void AStagingFileKeptByAFailedRotationIsFoldedInByTheNextOne()
    {
        // a failed rotation whose current log was re-created meanwhile keeps its staging file
        // under a name of this process; the next rotation folds it in
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path + ".1", "one|");
        File.WriteAllText(path + $".rotating-{Environment.ProcessId}-kept", "kept|");
        File.WriteAllText(path, "current|");

        Assert.True(new LogStore(_tempDir).Rotate("app.log", keep: 2).Ok);

        Assert.Equal("current|", File.ReadAllText(path + ".1"));
        Assert.Equal("one|kept|", File.ReadAllText(path + ".2"));
    }

    [Fact]
    public void FileLogStartupFoldsOrphansWithoutRotatingASmallLog()
    {
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path, "small|");
        File.WriteAllText(path + ".rotating", "orphan|");

        using (new FileLog(path, maxBytes: 1_000_000))
        {
        }

        Assert.Equal("small|", File.ReadAllText(path));
        Assert.Equal("orphan|", File.ReadAllText(path + ".1"));
        Assert.False(File.Exists(path + ".rotating"));
    }

    [Fact]
    public void ResolveRejectsPathsOutsideTheLogDirectory()
    {
        var result = new LogStore(_tempDir).Rotate("..\\other.log", keep: 2);
        Assert.False(result.Ok);
        Assert.Equal("unknown log name", result.Detail);
    }

    // ----- FileLog: manual and startup rotation -----

    [Fact]
    public void FileLogRotateNowMovesCurrentToAgeFileAndKeepsWriting()
    {
        var path = Path.Combine(_tempDir, "chain.log");
        using (var log = new FileLog(path, LogLevel.Info, maxBytes: 1_000_000, keep: 2))
        {
            log.Info("first");
            log.RotateNow();
            log.Info("second");
        }

        Assert.Contains("second", File.ReadAllText(path));
        Assert.Contains("first", File.ReadAllText(path + ".1"));
        Assert.DoesNotContain("first", File.ReadAllText(path));
    }

    [Fact]
    public void FileLogStartupRotationKicksInOnlyAboveThreshold()
    {
        var path = Path.Combine(_tempDir, "proxy.log");
        File.WriteAllText(path, new string('a', 100));
        File.WriteAllText(path + ".1", "previous");

        using (var log = new FileLog(path, LogLevel.Info, maxBytes: 50, keep: 2))
        {
            log.Info("fresh");
        }

        Assert.Contains("fresh", File.ReadAllText(path)); // a new file from scratch
        Assert.Contains(new string('a', 100), File.ReadAllText(path + ".1")); // the one over the threshold moved to .1
        Assert.Contains("previous", File.ReadAllText(path + ".2"));

        var small = Path.Combine(_tempDir, "small.log");
        File.WriteAllText(small, "tiny");
        using (var log = new FileLog(small, LogLevel.Info, maxBytes: 50, keep: 2))
        {
            log.Info("appended");
        }

        var text = File.ReadAllText(small); // below the threshold: the file was not shifted, writing continued
        Assert.Contains("tiny", text);
        Assert.Contains("appended", text);
        Assert.False(File.Exists(small + ".1"));
    }

    [Fact]
    public void FileLogWritersShareTheSingleFile()
    {
        var path = Path.Combine(_tempDir, "chain.log");
        using var first = new FileLog(path);
        first.Info("from first");

        using var second = new FileLog(path);
        second.Info("from second");

        Assert.Equal(path, first.FilePath);
        Assert.Equal(path, second.FilePath); // GUI in the tray + pnr from a terminal — one file, no PID copies
        Assert.Contains("from first", ReadShared(path));
        Assert.Contains("from second", ReadShared(path));
    }

    [Fact]
    public void FileLogRotatesBySizeDuringWrites()
    {
        var path = Path.Combine(_tempDir, "proxy.log");
        using var log = new FileLog(path, LogLevel.Info, maxBytes: 50, keep: 2);

        log.Info(new string('a', 60)); // over the threshold from the first record
        log.Info("after rotation");

        Assert.Contains(new string('a', 60), ReadShared(path + ".1")); // the overgrown file shifted...
        Assert.Contains("after rotation", ReadShared(path)); // ...writing continued in a fresh one
        Assert.DoesNotContain("after rotation", ReadShared(path + ".1"));
    }

    [Fact]
    public void AnotherFileLogNeverBlocksTheRotation()
    {
        // writers keep no handle between records (and share Delete), so a long-lived second
        // process cannot pin the file against the rotation any more
        var path = Path.Combine(_tempDir, "watchdog.log");
        using var other = new FileLog(path);
        other.Info("from the other writer");
        using var writer = new FileLog(path, LogLevel.Info, maxBytes: 50, keep: 2);

        writer.Info(new string('b', 60));
        writer.Info("after rotation");

        Assert.True(File.Exists(path + ".1"));
        Assert.Contains("after rotation", ReadShared(path));
        other.Info("the other writer follows the new file");
        Assert.Contains("the other writer follows the new file", ReadShared(path));
        Assert.DoesNotContain("the other writer follows the new file", ReadShared(path + ".1"));
    }

    /// <summary>
    /// Two processes that found the same overgrown file used to shift the chain one after the
    /// other: the second shift pushed the generation the first had just made out past keep. A
    /// size rotation re-checks the file under the rotation lock and leaves a rotated one alone.
    /// </summary>
    [Fact]
    public void ASizeRotationOfAnAlreadyRotatedLogShiftsNothing()
    {
        var path = Path.Combine(_tempDir, "proxy.log");
        File.WriteAllText(path + ".1", "older generation\n");
        File.WriteAllText(path, new string('a', 60));

        LogRotation.Shift(path, keep: 2, minBytes: 50); // the first process
        File.WriteAllText(path, "fresh records\n"); // its writers carry on in a new, small file
        LogRotation.Shift(path, keep: 2, minBytes: 50); // the second one, late with the same verdict

        Assert.Contains(new string('a', 60), ReadShared(path + ".1"));
        Assert.Contains("older generation", ReadShared(path + ".2")); // not pushed out past keep
        Assert.Contains("fresh records", ReadShared(path));
    }

    /// <summary>Another process's rotation in flight (its lock held) postpones this one; nothing moves meanwhile.</summary>
    [Fact]
    public void ARotationOfAnotherProcessInFlightPostponesTheSizeRotation()
    {
        var path = Path.Combine(_tempDir, "chain.log");
        using var writer = new FileLog(path, LogLevel.Info, maxBytes: 50, keep: 2, rotationRetryInterval: TimeSpan.Zero);
        var rotating = new FileStream(path + ".rotation-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

        writer.Info(new string('c', 60));
        writer.Info("still the same file");
        Assert.False(File.Exists(path + ".1"));
        Assert.False(writer.RotateNow());

        rotating.Dispose();
        writer.Info("after the other rotation");

        Assert.True(File.Exists(path + ".1"));
        Assert.Contains("after the other rotation", ReadShared(path));
    }

    [Fact]
    public void ManualRotationWithoutACurrentFileKeepsTheAgeChain()
    {
        var path = Path.Combine(_tempDir, "quiet.log");
        File.WriteAllText(path + ".1", "older generation\n");

        LogRotation.Shift(path, keep: 2);

        Assert.Contains("older generation", ReadShared(path + ".1"));
        Assert.False(File.Exists(path + ".2"));
    }

    [Fact]
    public void FileLogRetriesAPostponedRotationOnALaterRecord()
    {
        var path = Path.Combine(_tempDir, "chain.log");
        using var writer = new FileLog(path, LogLevel.Info, maxBytes: 50, keep: 2, rotationRetryInterval: TimeSpan.Zero);
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); // a viewer without Delete sharing
        writer.Info(new string('c', 60)); // over the threshold, the rename is refused
        writer.Info("still the same file");
        Assert.False(File.Exists(path + ".1"));
        Assert.Contains("still the same file", ReadShared(path));

        holder.Dispose();
        writer.Info("after the holder let go");

        Assert.True(File.Exists(path + ".1")); // not given up on for the process lifetime
        Assert.Contains("after the holder let go", ReadShared(path));
    }

    [Fact]
    public void RotateNowReportsAHeldFile()
    {
        var path = Path.Combine(_tempDir, "held.log");
        using var log = new FileLog(path);
        log.Info("x");
        using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.False(log.RotateNow());
        }

        Assert.True(log.RotateNow());
    }

    [Fact]
    public void ALogIoFailureDoesNotThrowOutOfWrite()
    {
        var path = Path.Combine(_tempDir, "dying.log");
        var log = new FileLog(path);
        File.Delete(path);
        Directory.CreateDirectory(path); // the log path can no longer be opened as a file
        using (log)
        {
            log.Info("this write fails, silently");
        }
    }
    /// <summary>Read a file opened by a writer (FileLog grants only FileShare.Read).</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
