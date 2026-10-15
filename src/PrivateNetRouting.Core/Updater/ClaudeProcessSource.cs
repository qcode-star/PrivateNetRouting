using System.ComponentModel;
using System.Diagnostics;

namespace PrivateNetRouting.Core.Updater;

/// <summary>A Claude process that can be asked to close or be killed.</summary>
public interface IClaudeProcessHandle
{
    bool CloseMainWindow();

    void Kill();
}

/// <summary>Claude Desktop processes by exact name (like Get-Process -Name).</summary>
public interface IClaudeProcessSource
{
    IReadOnlyList<IClaudeProcessHandle> Find(string processName);
}

/// <summary>
/// Processes by exact image name, optionally narrowed to one application by a fragment of the
/// image path (the MSIX install folder): the name alone is shared with unrelated programs — the
/// native Claude Code CLI is a `claude.exe`, the ChatGPT app shares the Codex executable name —
/// and the updater closes and tree-kills whatever this returns. With a marker set, a process
/// whose path cannot be read (an elevated one, a protected one) is left out: it is not the
/// per-user MSIX app the updater is after.
/// </summary>
public sealed class RunningClaudeProcessSource(string? imagePathMarker = null) : IClaudeProcessSource
{
    public IReadOnlyList<IClaudeProcessHandle> Find(string processName)
    {
        var handles = new List<IClaudeProcessHandle>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            if (imagePathMarker is null || ImagePathMatches(process, imagePathMarker))
            {
                handles.Add(new SystemProcess(process));
            }
            else
            {
                process.Dispose();
            }
        }

        return handles;
    }

    private static bool ImagePathMatches(Process process, string marker)
    {
        try
        {
            return process.MainModule?.FileName?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record SystemProcess(Process Process) : IClaudeProcessHandle
    {
        public bool CloseMainWindow() => Process.CloseMainWindow();

        public void Kill() => Process.Kill(entireProcessTree: true);
    }
}
