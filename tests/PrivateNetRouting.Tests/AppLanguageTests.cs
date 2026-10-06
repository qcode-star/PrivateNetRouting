using System.Globalization;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Tests;

/// <summary>UI language selection: auto means the system language, unsupported falls back to English.</summary>
public class AppLanguageTests
{
    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("en", "en")]
    [InlineData("RU", "ru")]
    [InlineData(" ru ", "ru")]
    [InlineData("ru-RU", "ru")]
    [InlineData("en-US", "en")]
    public void ExplicitPreferenceMatchesSupportedLanguage(string preference, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(preference, new CultureInfo("fr-FR")));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("de-DE")]
    [InlineData("klingon")]
    public void UnsupportedExplicitPreferenceFallsBackToEnglish(string preference)
    {
        Assert.Equal("en", AppLanguage.Resolve(preference, new CultureInfo("ru-RU")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void AutoUsesSystemLanguage(string? preference)
    {
        Assert.Equal("ru", AppLanguage.Resolve(preference, new CultureInfo("ru-RU")));
        Assert.Equal("en", AppLanguage.Resolve(preference, new CultureInfo("en-US")));
    }

    [Fact]
    public void AutoWithUnsupportedSystemLanguageFallsBackToEnglish()
    {
        Assert.Equal("en", AppLanguage.Resolve(AppLanguage.Auto, new CultureInfo("fr-FR")));
    }

    [Fact]
    public void RegionalSystemCultureUsesPrimaryTag()
    {
        Assert.Equal("ru", AppLanguage.Resolve(AppLanguage.Auto, new CultureInfo("ru-UA")));
    }
}
