using System.Diagnostics;

namespace PrivateNetRouting.Core.Updater;

/// <summary>Процесс Claude, который можно попросить закрыться или погасить.</summary>
public interface IClaudeProcessHandle
{
    bool CloseMainWindow();

    void Kill();
}

/// <summary>Процессы Claude Desktop по точному имени (как Get-Process -Name в прежней схеме).</summary>
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
