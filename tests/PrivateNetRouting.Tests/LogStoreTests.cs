using PrivateNetRouting.Core.Diagnostics;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Tests;

public class LogStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-logs-").FullName;

    // ----- список и хвост -----

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

    // ----- внешняя ротация -----

    [Fact]
    public void RotateShiftsAgeChainAndDropsBeyondKeep()
    {
        var store = new LogStore(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "proxy.log"), "current");
        File.WriteAllText(Path.Combine(_tempDir, "proxy.log.1"), "one");
        File.WriteAllText(Path.Combine(_tempDir, "proxy.log.2"), "two");

        var result = store.Rotate("proxy.log", keep: 2);

        Assert.True(result.Ok);
        Assert.Equal("current", File.ReadAllText(Path.Combine(_tempDir, "proxy.log.1"))); // текущий ушёл в .1
        Assert.Equal("one", File.ReadAllText(Path.Combine(_tempDir, "proxy.log.2"))); // старый .1 сдвинулся в .2
        Assert.False(File.Exists(Path.Combine(_tempDir, "proxy.log")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "proxy.log.3"))); // старый .2 («two») вытолкнут за keep
    }

    [Fact]
    public void RotateMissingFileIsNothingToDo()
    {
        var result = new LogStore(_tempDir).Rotate("absent.log", keep: 2);
        Assert.True(result.Ok);
        Assert.Contains("does not exist", result.Detail);
    }

    [Fact]
    public void RotateFileHeldOpenByAnotherProcessFails()
    {
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path, "held");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); // как живой FileLog

        var result = new LogStore(_tempDir).Rotate("app.log", keep: 2);

        Assert.False(result.Ok);
        Assert.Contains("locked", result.Detail);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);
        Assert.Equal("held", text.ReadToEnd()); // файл не тронут
    }

    [Fact]
    public void ResolveRejectsPathsOutsideTheLogDirectory()
    {
        var result = new LogStore(_tempDir).Rotate("..\\other.log", keep: 2);
        Assert.False(result.Ok);
        Assert.Equal("unknown log name", result.Detail);
    }

    // ----- FileLog: ручная и стартовая ротация -----

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

        Assert.Contains("fresh", File.ReadAllText(path)); // новый файл с нуля
        Assert.Contains(new string('a', 100), File.ReadAllText(path + ".1")); // переросший порог ушёл в .1
        Assert.Contains("previous", File.ReadAllText(path + ".2"));

        var small = Path.Combine(_tempDir, "small.log");
        File.WriteAllText(small, "tiny");
        using (var log = new FileLog(small, LogLevel.Info, maxBytes: 50, keep: 2))
        {
            log.Info("appended");
        }

        var text = File.ReadAllText(small); // ниже порога: файл не сдвигался, запись продолжена
        Assert.Contains("tiny", text);
        Assert.Contains("appended", text);
        Assert.False(File.Exists(small + ".1"));
    }

    [Fact]
    public void FileLogFallsBackToPerProcessFileWhenPrimaryIsHeld()
    {
        var path = Path.Combine(_tempDir, "chain.log");
        using var first = new FileLog(path);
        first.Info("from first");

        using var second = new FileLog(path);
        second.Info("from second");

        Assert.Equal(path, first.FilePath);
        Assert.NotEqual(path, second.FilePath);
        Assert.EndsWith($".{Environment.ProcessId}.log", second.FilePath); // GUI в трее + pnr из терминала
        Assert.Contains("from first", ReadShared(path));
        Assert.Contains("from second", ReadShared(second.FilePath));
        Assert.Contains("held by another process", ReadShared(second.FilePath));
    }

    /// <summary>Чтение файла, открытого писателем (FileLog даёт только FileShare.Read).</summary>
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
