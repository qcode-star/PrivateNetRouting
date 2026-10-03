using System.IO.Compression;
using System.Xml.Linq;

namespace PrivateNetRouting.Core.Updater;

/// <summary>Identity из AppxManifest.xml пакета (сверяется с установленным перед установкой).</summary>
public sealed record MsixIdentity(string Name, string Version, string Publisher, string Architecture);

public static class MsixPackage
{
    /// <summary>
    /// Читает Identity из AppxManifest.xml внутри пакета; null — манифеста нет (не одиночный
    /// MSIX, вручную не поставить).
    /// </summary>
    public static MsixIdentity? TryReadIdentity(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var entry = zip.GetEntry("AppxManifest.xml");
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        var root = XDocument.Load(stream).Root
            ?? throw new IOException("AppxManifest.xml has no root element");
        var identity = root.Element(XName.Get("Identity", root.Name.NamespaceName))
            ?? throw new IOException("AppxManifest.xml has no Identity element");
        return new MsixIdentity(
            (string?)identity.Attribute("Name") ?? "",
            (string?)identity.Attribute("Version") ?? "",
            (string?)identity.Attribute("Publisher") ?? "",
            (string?)identity.Attribute("ProcessorArchitecture") ?? "");
    }
}
