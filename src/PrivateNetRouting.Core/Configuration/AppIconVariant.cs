namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// The icon identity of a running profile: the exe carries the neutral icon, and a profile that
/// serves one desktop app wears that app's brand icon, so two profiles running side by side are
/// told apart on the taskbar, in the tray and on the desktop.
/// </summary>
public enum AppIconVariant
{
    /// <summary>Both apps are enabled: the neutral icon with both brand colours.</summary>
    Both,

    /// <summary>Only Claude is enabled: the Claude brand icon.</summary>
    Claude,

    /// <summary>Only Codex is enabled: the Codex brand icon.</summary>
    Codex,
}

/// <summary>Which icon identity a profile runs with.</summary>
public static class AppIconVariants
{
    /// <summary>
    /// A profile that enables exactly one desktop app wears that app's brand icon. Both enabled
    /// (nothing to tell apart) and both disabled keep the neutral one — a disabled app is not the
    /// profile's identity either.
    /// </summary>
    public static AppIconVariant Resolve(bool claudeEnabled, bool codexEnabled) =>
        (claudeEnabled, codexEnabled) switch
        {
            (true, false) => AppIconVariant.Claude,
            (false, true) => AppIconVariant.Codex,
            _ => AppIconVariant.Both,
        };

    public static AppIconVariant Resolve(AppConfig config) =>
        Resolve(config.Claude.Enabled, config.Codex.Enabled);
}
