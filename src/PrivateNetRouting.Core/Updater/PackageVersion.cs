namespace PrivateNetRouting.Core.Updater;

/// <summary>
/// Ordering of package versions. The formats differ between sources — the update URL carries
/// 2.26454.2, the installed package and the MSIX identity 2.26454.2.0 — so the components are
/// padded to four before comparing (System.Version treats a missing component as lower).
/// </summary>
internal static class PackageVersion
{
    /// <summary>Negative: left is older, 0: equal, positive: left is newer; null when either side does not parse.</summary>
    public static int? Compare(string left, string right)
    {
        if (!TryParse(left, out var l) || !TryParse(right, out var r))
        {
            return null;
        }

        return l.CompareTo(r);
    }

    private static bool TryParse(string text, out Version version)
    {
        if (Version.TryParse(text.Trim(), out var parsed) && parsed.Major >= 0 && parsed.Minor >= 0)
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
            return true;
        }

        version = new Version();
        return false;
    }
}
