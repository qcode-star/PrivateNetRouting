using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PrivateNetRouting.App.Localization;

namespace PrivateNetRouting.App;

/// <summary>
/// A localized message as inline runs: the words keep the interface face, the substituted values
/// (sizes, versions, counters — machine data) go in the monospace face. Together with
/// <see cref="Pad"/> a value holds a fixed width, so in a line that updates live the words
/// stand still and only the digits change.
/// </summary>
internal static partial class FormattedRuns
{
    /// <summary>Field width of a padded value: the widest machine value of the live lines.</summary>
    private const int FieldWidth = 9;

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>A machine value right-aligned in a fixed field, so the line around it does not shift.</summary>
    public static string Pad(string value, int width = FieldWidth) => value.PadLeft(width);

    /// <summary>A machine number right-aligned in a fixed field.</summary>
    public static string Pad(int value, int width = FieldWidth) =>
        Pad(value.ToString(CultureInfo.InvariantCulture), width);

    /// <summary>The message by key as runs: literals in the caller's face, values monospaced.</summary>
    public static Inline[] Build(string key, params object?[] args)
    {
        var template = Loc.S(key);
        var matches = PlaceholderPattern().Matches(template);
        if (matches.Any(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= args.Length))
        {
            // a template the arguments do not fit: show the plain formatted message
            return [new Run(Loc.F(key, args))];
        }

        var runs = new List<Inline>();
        var position = 0;
        foreach (Match match in matches)
        {
            if (match.Index > position)
            {
                runs.Add(new Run(template[position..match.Index]));
            }

            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            runs.Add(new Run(string.Format(CultureInfo.InvariantCulture, "{0}", args[index]))
            {
                FontFamily = Theme.Mono,
            });
            position = match.Index + match.Length;
        }

        if (position < template.Length)
        {
            runs.Add(new Run(template[position..]));
        }

        return [.. runs];
    }
}
