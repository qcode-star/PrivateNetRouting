using System.Diagnostics;
using PrivateNetRouting.Core.Updater;

namespace PrivateNetRouting.Tests;

/// <summary>The updaters' process source: the image path narrows a shared executable name to one application.</summary>
public class ProcessSourceTests
{
    [Fact]
    public void WithoutAMarkerEveryProcessOfTheNameIsReturned()
    {
        using var self = Process.GetCurrentProcess();

        var found = new RunningClaudeProcessSource().Find(self.ProcessName);

        Assert.NotEmpty(found);
    }

    [Fact]
    public void AMatchingImagePathMarkerKeepsTheProcess()
    {
        using var self = Process.GetCurrentProcess();
        var folder = Path.GetDirectoryName(self.MainModule!.FileName)!;

        var found = new RunningClaudeProcessSource(folder).Find(self.ProcessName);

        Assert.NotEmpty(found);
    }

    [Fact]
    public void AForeignImagePathDropsTheProcessEvenWithTheSameName()
    {
        // an unrelated application sharing the image name (ChatGPT/Codex, claude.exe CLI/Desktop)
        using var self = Process.GetCurrentProcess();

        var found = new RunningClaudeProcessSource(@"\WindowsApps\Some.Other.Package_").Find(self.ProcessName);

        Assert.Empty(found);
    }
}
