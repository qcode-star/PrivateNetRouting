using System.Globalization;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// Application UI language selection. Supported languages are listed here explicitly:
/// a new language is a Strings.&lt;code&gt;.xaml dictionary in the application plus a code in
/// <see cref="Supported"/>. Scheme: "auto" (or empty) — the system language; anything
/// unrecognized or unsupported — English as a fallback.
/// </summary>
public static class AppLanguage
{
    public const string Auto = "auto";

    /// <summary>Fallback if both the preferred and the system languages are unsupported.</summary>
    public const string Fallback = "en";

    /// <summary>Two-letter language codes for which the application has string dictionaries.</summary>
    public static readonly IReadOnlyList<string> Supported = ["en", "ru"];

    /// <summary>
    /// Returns the code of a supported language: an exact match with the preference or its
    /// primary subtag (ru-RU → ru), for auto — the system language, otherwise the fallback.
    /// </summary>
    public static string Resolve(string? preference, CultureInfo systemUiCulture)
    {
        var value = preference?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value == Auto)
        {
            return Match(systemUiCulture.Name) ?? Fallback;
        }

        return Match(value) ?? Fallback;
    }

    private static string? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var primary = name.Trim().ToLowerInvariant().Split('-')[0];
        return Supported.Contains(primary) ? primary : null;
    }
}
