using System.Text.RegularExpressions;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// What the elevated helper must not take on trust from the user config: that file is writable
/// at medium integrity, while the helper acts as an administrator. The binaries and files it
/// executes or overwrites are pinned to system locations, its log and result file live in the
/// pinned elevated directory under ProgramData (a UNC path would make the helper authenticate or
/// write on a network share as administrator), and the result file it writes is restricted to the
/// one shape the application itself creates.
/// </summary>
public static partial class ElevatedHelperTrust
{
    /// <summary>
    /// Tests redirect the pinned root to a temporary directory; null — the real one. Tests that
    /// touch it run serialized (a common xUnit collection), because the override is process-wide.
    /// </summary>
    internal static string? PinnedRootOverride;

    /// <summary>
    /// The only directory the helper writes to: its log and the result files it reports through.
    /// ProgramData is machine-local, so nothing here can name a network share.
    /// </summary>
    public static string PinnedDirectory =>
        Path.Combine(
            PinnedRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PrivateNetRouting",
            "elevated");

    /// <summary>The result file the application creates: `&lt;command&gt;=&lt;8 hex&gt;.result`.</summary>
    [GeneratedRegex(@"^[a-z0-9-]+=[0-9a-f]{8}\.result$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultFileName();

    /// <summary>
    /// Only a file of our own shape inside the pinned directory is written; anything else —
    /// another directory, a UNC or device path — is no result file of ours.
    /// </summary>
    public static bool IsValidResultFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (!ResultFileName().IsMatch(Path.GetFileName(path)))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(path).StartsWith(
                PinnedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The config the helper loads: an absolute path to a .json file on a local drive (what the
    /// application itself passes). A UNC path would make the helper authenticate against a
    /// server the config named.
    /// </summary>
    public static bool IsValidConfigPath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && IsLocalDriveRoot(path);

    /// <summary>
    /// The statedir the helper creates and starts the daemon with: an absolute local path that
    /// cannot change a command line (the same inertness the start lines require) and cannot name
    /// a network share or a device. It is not pinned — the non-elevated side finds and stops the
    /// daemon by the statedir in its command line, so helper and config must agree on it.
    /// </summary>
    public static bool IsValidStateDir(string path) =>
        CommandLineSafety.IsInert(path)
        && Path.IsPathFullyQualified(path)
        && IsLocalDriveRoot(path);

    /// <summary>False for UNC roots (\\server\share) and device form paths (\\.\, \\?\).</summary>
    private static bool IsLocalDriveRoot(string path)
    {
        try
        {
            return !Path.GetPathRoot(Path.GetFullPath(path))!.StartsWith(@"\\", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The helper's log is pinned to the elevated directory — an administrator must not write
    /// wherever the user-writable config names. A differing `logging.directory` is warned about
    /// into the pinned log itself, the way PinSystemLocations reports ignored executables.
    /// </summary>
    public static FileLog OpenPinnedLog(AppConfig config, string fileName)
    {
        var log = new FileLog(Path.Combine(PinnedDirectory, fileName), config.Logging.EffectiveLevel);
        var configured = config.Logging.Directory;
        string? configuredFull = null;
        try
        {
            configuredFull = Path.GetFullPath(configured);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // an unusable value differs from the pinned directory all the same
        }

        if (!string.Equals(configuredFull, PinnedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"elevated helper: logging.directory '{configured}' ignored, using the pinned elevated log");
        }

        return log;
    }

    /// <summary>
    /// Writes a result file so that the waiting parent never sees half of it: a temp file next to
    /// it, then a rename over the target. The pinned directory is created on the way: the asking
    /// side creates it before the helper runs, and the helper reports through this file even when
    /// that never happened (a cancelled wait, a fresh machine, the in-process path).
    /// </summary>
    public static void WriteResultFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
