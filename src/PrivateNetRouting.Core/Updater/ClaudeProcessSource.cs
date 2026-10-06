using System.Diagnostics;

namespace PrivateNetRouting.Core.Updater;

/// <summary>A Claude process that can be asked to close or be killed.</summary>
public interface IClaudeProcessHandle
{
    bool CloseMainWindow();

    void Kill();
}

/// <summary>Claude Desktop processes by exact name (like Get-Process -Name in the legacy scheme).</summary>
public interface IClaudeProcessSource
{
    IReadOnlyList<IClaudeProcessHandle> Find(string processName);
}

public sealed class RunningClaudeProcessSource : IClaudeProcessSource
{
    public IReadOnlyList<IClaudeProcessHandle> Find(string processName) =>
        Process.GetProcessesByName(processName)
            .Select(process => (IClaudeProcessHandle)new SystemProcess(process))
            .ToList();

    private sealed record SystemProcess(Process Process) : IClaudeProcessHandle
    {
        public bool CloseMainWindow() => Process.CloseMainWindow();

        public void Kill() => Process.Kill(entireProcessTree: true);
    }
}
