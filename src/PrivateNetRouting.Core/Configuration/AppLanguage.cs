using System.Globalization;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// Выбор языка интерфейса приложения. Поддержанные языки перечислены здесь явно:
/// новый язык — это словарь Strings.&lt;код&gt;.xaml в приложении плюс код в
/// <see cref="Supported"/>. Схема: "auto" (или пусто) — язык системы; всё
/// нераспознанное и неподдержанное — английский фолбэком.
/// </summary>
public static class AppLanguage
{
    public const string Auto = "auto";

    /// <summary>Фолбэк, если предпочитаемый и системный языки не поддержаны.</summary>
    public const string Fallback = "en";

    /// <summary>Двухбуквенные коды языков, для которых в приложении есть словари строк.</summary>
    public static readonly IReadOnlyList<string> Supported = ["en", "ru"];

    /// <summary>
    /// Возвращает код поддержанного языка: точное совпадение с предпочтением или его
    /// первичная подсказка (ru-RU → ru), для auto — язык системы, иначе фолбэк.
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
