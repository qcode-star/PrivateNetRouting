using System.Text;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// Replaces a text file in one step: the content goes to a unique temp file in the target's
/// directory and is File.Replace'd over it — the ACL and attributes stay with the file, and a
/// crash, power loss or a concurrent reader never sees a truncated file (which the truncating
/// WriteAllText this replaces did not guarantee). A missing target is created by File.Move; a
/// symlink is resolved to its final target first (a Replace over the link would swap the link
/// itself). The text is written UTF-8; a UTF-8 BOM of the existing file is preserved.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var target = ResolveWriteTarget(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            // UTF-8 without BOM by default; a file that had one keeps it (the temp carries it,
            // the Replace swaps the whole file, so the BOM never disappears mid-history)
            var encoding = HasUtf8Bom(target) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : new UTF8Encoding(false);
            File.WriteAllText(temp, content, encoding);
            if (File.Exists(target))
            {
                File.Replace(temp, target, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the temp file is leftover garbage, not the target
            }

            throw;
        }
    }

    /// <summary>The file a write lands in: the final target of a symlink, the path itself otherwise.</summary>
    internal static string ResolveWriteTarget(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full; // a link loop or an unreadable reparse point: write the path itself
        }
    }

    private static bool HasUtf8Bom(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[3];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == 3
                && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // unreadable or momentarily held: write without a BOM, as before
        }
    }
}
