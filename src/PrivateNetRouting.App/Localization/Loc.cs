using System.Globalization;
using System.Windows;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.App.Localization;

/// <summary>
/// Application UI language: swaps the Strings.&lt;lang&gt;.xaml dictionary in the resources
/// (DynamicResource in XAML picks up the swap on the fly) and hands strings to code
/// (<see cref="S"/> — by key, <see cref="F"/> — format template with substitutions).
/// New language: a Strings.&lt;code&gt;.xaml dictionary plus a code in <see cref="AppLanguage.Supported"/>.
/// The CLI (pnr) is always English and does not use this service.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, ResourceDictionary> Cache = [];

    /// <summary>Fires after the dictionary swap: view models redraw their strings.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Code of the current language ("en", "ru").</summary>
    public static string CurrentLanguage { get; private set; } = AppLanguage.Fallback;

    /// <summary>Swaps the string dictionary; an unsupported code quietly falls back.</summary>
    public static void Apply(string language)
    {
        language = AppLanguage.Supported.Contains(language) ? language : AppLanguage.Fallback;
        var resources = Application.Current.Resources;
        var fresh = GetDictionary(language);
        for (var i = 0; i < resources.MergedDictionaries.Count; i++)
        {
            if (IsStringsDictionary(resources.MergedDictionaries[i]))
            {
                resources.MergedDictionaries[i] = fresh;
                break;
            }
        }

        if (!resources.MergedDictionaries.Contains(fresh))
        {
            resources.MergedDictionaries.Add(fresh);
        }

        CurrentLanguage = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>String by key; a missing key shows in the UI as [key].</summary>
    public static string S(string key) =>
        Application.Current.TryFindResource(key) as string ?? $"[{key}]";

    /// <summary>Format template by key + substitutions (placeholders {0}, {1}, ...).</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, S(key), args);

    private static bool IsStringsDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.Contains("/Localization/Strings.", StringComparison.Ordinal) == true;

    private static ResourceDictionary GetDictionary(string language)
    {
        if (Cache.TryGetValue(language, out var cached))
        {
            return cached;
        }

        var uri = new Uri(
            $"pack://application:,,,/PrivateNetRouting.App;component/Localization/Strings.{language}.xaml",
            UriKind.Absolute);
        var dictionary = new ResourceDictionary { Source = uri };
        Cache[language] = dictionary;
        return dictionary;
    }
}
