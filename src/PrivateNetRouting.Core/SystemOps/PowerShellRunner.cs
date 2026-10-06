using System.Diagnostics;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// Launching Windows PowerShell without a window (updater: Get-AppxPackage/Add-AppxPackage/Get-WinEvent;
/// guard: Get-DnsClientNrptRule/NetFirewall). PowerShell 5.1 is an OS component,
/// the path is taken from the config (updater.powerShellPath).
/// </summary>
public interface IPowerShellExecutor
{
    Task<(int Code, string Output)> RunScriptAsync(string script, CancellationToken ct);
}

public sealed class PowerShellRunner(string powerShellPath) : IPowerShellExecutor
{
    public async Task<(int Code, string Output)> RunScriptAsync(string script, CancellationToken ct)
    {
        var info = new ProcessStartInfo(powerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        using var process = Process.Start(info)
            ?? throw new IOException($"failed to start {powerShellPath}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // the process has already exited on its own
            }

            throw;
        }

        var output = (await stdoutTask + await stderrTask).Trim();
        return (process.ExitCode, output);
    }
}
