using System.Text.RegularExpressions;
using System.Xml.Linq;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Tests;

/// <summary>
/// Словари локализации держат одинаковый с английским базовым набор ключей и
/// плейсхолдеров — иначе смена языка оставит в UI дырки ([ключ] или кривой формат).
/// Заодно список AppLanguage.Supported сверяется с файлами словарей.
/// </summary>
public class LocalizationDictionariesTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var language in DictionaryLanguages().Where(l => l != AppLanguage.Fallback))
        {
            data.Add(language);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void DictionaryMatchesEnglishBaseline(string language)
    {
        var baseline = Load(AppLanguage.Fallback);
        var actual = Load(language);

        Assert.Equal(baseline.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
        foreach (var (key, value) in actual)
        {
            Assert.Equal(Placeholders(baseline[key]), Placeholders(value));
        }
    }

    [Fact]
    public void SupportedListMatchesDictionaries()
    {
        Assert.Equal(
            AppLanguage.Supported.OrderBy(language => language),
            DictionaryLanguages().OrderBy(language => language));
    }

    private static IEnumerable<string> DictionaryLanguages()
    {
        var directory = Path.Combine(
            RepoRoot(), "src", "PrivateNetRouting.App", "Localization");
        return Directory.EnumerateFiles(directory, "Strings.*.xaml")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name!["Strings.".Length..]);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PrivateNetRouting.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static Dictionary<string, string> Load(string language)
    {
        var path = Path.Combine(
            RepoRoot(), "src", "PrivateNetRouting.App", "Localization", $"Strings.{language}.xaml");
        return XDocument.Load(path)
            .Descendants(XName.Get("String", XamlNamespace))
            .ToDictionary(
                element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)) ?? "",
                element => element.Value);
    }

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+(?::[^}]*)?\}")
            .Select(match => match.Value.Split(':')[0])
            .OrderBy(placeholder => placeholder)
            .ToArray();
}
