using System.Text.Json;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Updater;

/// <summary>Установленный MSIX-пакет Claude Desktop (сведения Get-AppxPackage).</summary>
public sealed record InstalledPackage(string Name, string Version, string Publisher, string InstallLocation);

/// <summary>Сведения об установленном пакете и установка (прав администратора не требует).</summary>
public interface IAppxPackageSource
{
    Task<InstalledPackage?> GetInstalledAsync(string packageName, CancellationToken ct);

    Task AddPackageAsync(string packageFile, CancellationToken ct);
}

/// <summary>
/// Get-AppxPackage (версия/Publisher установленного пакета; при нескольких берётся старшая)
/// и Add-AppxPackage -ForceApplicationShutdown — как в прежней схеме обновления.
/// </summary>
public sealed class PowerShellAppxPackageSource(IPowerShellExecutor powershell, FileLog log) : IAppxPackageSource
{
    public async Task<InstalledPackage?> GetInstalledAsync(string packageName, CancellationToken ct)
    {
        var script =
            "Get-AppxPackage -Name '" + packageName.Replace("'", "''") + "' | " +
            "Sort-Object Version -Descending | " +
            "Select-Object -First 1 -Property Name,Version,Publisher,InstallLocation | " +
            "ConvertTo-Json -Compress";
        var (code, output) = await powershell.RunScriptAsync(script, ct);
        if (code != 0)
        {
            throw new IOException($"Get-AppxPackage failed (exit {code}): {output}");
        }

        return ParsePackageJson(output);
    }

    public async Task AddPackageAsync(string packageFile, CancellationToken ct)
    {
        var script = "Add-AppxPackage -Path '" + packageFile.Replace("'", "''") + "' -ForceApplicationShutdown";
        var (code, output) = await powershell.RunScriptAsync(script, ct);
        if (code != 0)
        {
            throw new IOException(
                $"Add-AppxPackage failed (exit {code}): {output}\n" +
                $"If it complains about rights, run from an admin console:\n" +
                $"  Add-AppxPackage -Path \"{packageFile}\" -ForceApplicationShutdown");
        }

        log.Info($"updater: Add-AppxPackage ok: {packageFile}");
    }

    /// <summary>JSON одиночного пакета от ConvertTo-Json; пустой вывод — пакета нет.</summary>
    internal static InstalledPackage? ParsePackageJson(string json)
    {
        var trimmed = json.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(trimmed);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new InstalledPackage(
            GetString("Name"),
            GetString("Version"),
            GetString("Publisher"),
            GetString("InstallLocation"));

        string GetString(string property) =>
            document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
    }
}
