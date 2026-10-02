using System.Globalization;
using System.Windows;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.App.Localization;

/// <summary>
/// Язык интерфейса приложения: подменяет словарь Strings.&lt;lang&gt;.xaml в ресурсах
/// (DynamicResource в XAML подхватывает подмену на лету) и раздаёт строки коду
/// (<see cref="S"/> — по ключу, <see cref="F"/> — формат-шаблон с подстановками).
/// Новый язык: словарь Strings.&lt;код&gt;.xaml плюс код в <see cref="AppLanguage.Supported"/>.
/// CLI (pnr) всегда английский и этим сервисом не пользуется.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, ResourceDictionary> Cache = [];

    /// <summary>Срабатывает после смены словаря: ViewModel-и перерисовывают свои строки.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Код текущего языка ("en", "ru").</summary>
    public static string CurrentLanguage { get; private set; } = AppLanguage.Fallback;

    /// <summary>Подменяет словарь строк; неподдержанный код молча даёт фолбэк.</summary>
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

    /// <summary>Строка по ключу; отсутствующий ключ виден в UI как [ключ].</summary>
    public static string S(string key) =>
        Application.Current.TryFindResource(key) as string ?? $"[{key}]";

    /// <summary>Формат-шаблон по ключу + подстановки (плейсхолдеры {0}, {1}, ...).</summary>
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
