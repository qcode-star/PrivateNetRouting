using System.Text.Json;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Updater;

/// <summary>Installed Claude Desktop MSIX package (information from Get-AppxPackage).</summary>
public sealed record InstalledPackage(string Name, string Version, string Publisher, string InstallLocation);

/// <summary>Information about the installed package and installation (requires no admin rights).</summary>
public interface IAppxPackageSource
{
    Task<InstalledPackage?> GetInstalledAsync(string packageName, CancellationToken ct);

    Task AddPackageAsync(string packageFile, CancellationToken ct);
}

/// <summary>
/// Get-AppxPackage (version/Publisher of the installed package; with several, the highest is taken)
/// and Add-AppxPackage -ForceApplicationShutdown.
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

    /// <summary>JSON of a single package from ConvertTo-Json; empty output — no package.</summary>
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
