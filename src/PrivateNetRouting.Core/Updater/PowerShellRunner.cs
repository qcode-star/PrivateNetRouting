using System.Diagnostics;

namespace PrivateNetRouting.Core.Updater;

/// <summary>
/// Запуск Windows PowerShell без окна: Get-AppxPackage/Add-AppxPackage/Get-WinEvent —
/// те же командлеты, что в прежней схеме обновления. PowerShell 5.1 — компонент ОС,
/// путь берётся из конфига (updater.powerShellPath).
/// </summary>
public sealed class PowerShellRunner(string powerShellPath)
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
                // процесс уже завершился сам
            }

            throw;
        }

        var output = (await stdoutTask + await stderrTask).Trim();
        return (process.ExitCode, output);
    }
}
