using System.Text.RegularExpressions;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// What the elevated helper must not take on trust from the user config: that file is writable
/// at medium integrity, while the helper acts as an administrator. The binaries and files it
/// executes or overwrites are pinned to system locations, and the result file it writes is
/// restricted to the one shape the application itself creates.
/// </summary>
public static partial class ElevatedHelperTrust
{
    /// <summary>The result file the application creates: `&lt;command&gt;=&lt;8 hex&gt;.result`.</summary>
    [GeneratedRegex(@"^[a-z0-9-]+=[0-9a-f]{8}\.result$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultFileName();

    /// <summary>Only an absolute path to such a file is written; anything else is no result file of ours.</summary>
    public static bool IsValidResultFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        return ResultFileName().IsMatch(Path.GetFileName(path));
    }

    /// <summary>The config the helper loads: an absolute path to a .json file (what the application itself passes).</summary>
    public static bool IsValidConfigPath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes a result file so that the waiting parent never sees half of it: a temp file next to
    /// it, then a rename over the target.
    /// </summary>
    public static void WriteResultFile(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Windows PowerShell of the system directory — the one the cmdlets of the ban layers belong to.</summary>
    public static string SystemPowerShell() =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>The system hosts file.</summary>
    public static string SystemHostsFile() =>
        Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    /// <summary>The standard per-machine Tailscale install — the only binaries the elevated chain helper executes.</summary>
    public static string SystemTailscaled() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscaled.exe");

    public static string SystemTailscaleCli() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
}
