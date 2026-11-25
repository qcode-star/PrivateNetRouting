using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.SystemOps;

/// <summary>
/// The icon files the desktop shortcuts point at. A shortcut's icon is a file path, while the exe
/// carries one icon only (the neutral "both apps" one), so a single-app profile writes its brand
/// variant here; the folder is per user and shared by the profiles (the file names differ).
/// </summary>
public static class AppIconFiles
{
    /// <summary>%APPDATA%\PrivateNetRouting\icons — next to the config, not in the install folder (no elevation).</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PrivateNetRouting",
        "icons");

    /// <summary>
    /// The variant's icon file, or null when the exe's own icon is the right one. The file names
    /// are the packaged asset names (Assets\*.ico): one name for the resource and the extracted file.
    /// </summary>
    public static string? PathFor(AppIconVariant variant) => variant switch
    {
        AppIconVariant.Claude => Path.Combine(Directory, "app-claude.ico"),
        AppIconVariant.Codex => Path.Combine(Directory, "app-codex.ico"),
        _ => null,
    };
}
