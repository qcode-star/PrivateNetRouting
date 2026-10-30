using System.Windows;
using System.Windows.Media;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core.Guard;

namespace PrivateNetRouting.App;

/// <summary>
/// Tokens of App.xaml for the code paths (view models and the windows that draw state by hand):
/// a mark or a state word always resolves to the same brush the markup uses, so a single edit
/// in App.xaml moves the whole application. Call on the UI thread.
/// </summary>
internal static class Theme
{
    public static Brush Of(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Machine-data typeface (monospaced, tabular figures).</summary>
    public static FontFamily Mono => (FontFamily)Application.Current.FindResource("Font.Mono");

    public static Brush Ok => Of("Brush.Ok");

    public static Brush Warn => Of("Brush.Warn");

    public static Brush Bad => Of("Brush.Bad");

    public static Brush Idle => Of("Brush.Idle");

    public static Brush Accent => Of("Brush.Accent");

    public static Brush Text => Of("Brush.Text");

    public static Brush TextSecondary => Of("Brush.TextSecondary");

    public static Brush TextMuted => Of("Brush.TextMuted");

    /// <summary>Brush of a guard layer: on — good, partial — warning, off — at risk.</summary>
    public static Brush GuardBrush(GuardState state) => state switch
    {
        GuardState.On => Ok,
        GuardState.Partial => Warn,
        GuardState.Off => Bad,
        _ => Idle,
    };

    /// <summary>Word for a guard layer state — state is never carried by colour alone.</summary>
    public static string GuardWord(GuardState state) => state switch
    {
        GuardState.On => Loc.S("Main.Guard.State.On"),
        GuardState.Off => Loc.S("Main.Guard.State.Off"),
        GuardState.Partial => Loc.S("Main.Guard.State.Partial"),
        _ => Loc.S("Main.Guard.State.Unknown"),
    };
}
