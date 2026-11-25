using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Tests;

/// <summary>
/// The profile option: parsing (anywhere among the arguments, one option, a validated name),
/// path/identity resolution, the display name and the "a named profile must exist" rule.
/// </summary>
public class AppProfileTests
{
    [Fact]
    public void NoOptionIsTheDefaultProfile()
    {
        Assert.Null(AppProfile.ParseName(["chain", "up"]));
        Assert.Equal(ProfileContext.Default, AppProfile.Resolve(null));
        Assert.True(ProfileContext.Default.IsDefault);
        Assert.Null(ProfileContext.Default.NameSuffix);
        Assert.EndsWith(@"PrivateNetRouting\appsettings.json", ProfileContext.Default.ConfigPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOptionIsFoundAnywhereAndTheRestKeepsItsOrder()
    {
        Assert.Equal("codex", AppProfile.ParseName(["--profile", "codex", "chain", "up"]));
        Assert.Equal("codex", AppProfile.ParseName(["chain", "up", "--profile", "codex"]));
        Assert.Equal("codex", AppProfile.ParseName(["--PROFILE", "codex"]));
        Assert.Equal(["chain", "up"], AppProfile.WithoutOption(["--profile", "codex", "chain", "up"]));
        Assert.Equal(["watchdog"], AppProfile.WithoutOption(["watchdog", "--profile", "codex"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" default ")]
    [InlineData("../codex")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("-codex")]
    [InlineData("codex.")]
    [InlineData("codex:1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AnUnusableNameIsRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => AppProfile.ParseName(["--profile", name]));
    }

    [Fact]
    public void AMissingValueAndTwoDifferentNamesAreErrors()
    {
        Assert.Throws<ArgumentException>(() => AppProfile.ParseName(["chain", "--profile"]));
        Assert.Throws<ArgumentException>(() => AppProfile.ParseName(["--profile", "codex", "--profile", "other"]));
    }

    [Fact]
    public void ResolveNamesTheFileAndScopesTheIdentity()
    {
        var profile = AppProfile.Resolve("Codex");

        Assert.False(profile.IsDefault);
        Assert.Equal("codex", profile.Name);
        Assert.Equal("codex", profile.NameSuffix);
        Assert.Equal("Codex", profile.Label);
        Assert.EndsWith(@"profiles\codex.json", profile.ConfigPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisplayNamePrefersTheConfigAndFallsBackToTheTypedName()
    {
        var named = AppProfile.Resolve("Codex");
        var defaultProfile = ProfileContext.Default;

        Assert.Equal("Codex", named.DisplayName(new AppConfig()));
        Assert.Equal("Codex KZ", named.DisplayName(new AppConfig { Profile = new ProfileConfig { Name = " Codex KZ " } }));
        Assert.Null(defaultProfile.DisplayName(new AppConfig()));
        Assert.Equal("Claude", defaultProfile.DisplayName(new AppConfig { Profile = new ProfileConfig { Name = "Claude" } }));
    }

    [Fact]
    public void ACommandLineNamesItsProfile()
    {
        Assert.Equal("codex", AppProfile.NameFromArguments([@"C:\app\pnr.exe", "--watchdog-worker", "--profile", "codex"]));
        Assert.Null(AppProfile.NameFromArguments([@"C:\app\pnr.exe", "--watchdog-worker"]));
        Assert.Null(AppProfile.NameFromArguments([@"C:\app\pnr.exe", "--profile"])); // no value, not a pair
    }

    [Fact]
    public void ANamedProfileMustExistButTheDefaultOneMayAppear()
    {
        AppProfile.EnsureConfigured(ProfileContext.Default); // no file needed

        var missing = new ProfileContext("codex", Path.Combine(Path.GetTempPath(), $"pnr-missing-{Guid.NewGuid():N}.json"));
        Assert.Throws<FileNotFoundException>(() => AppProfile.EnsureConfigured(missing));

        File.WriteAllText(missing.ConfigPath, "{}");
        try
        {
            AppProfile.EnsureConfigured(missing);
        }
        finally
        {
            File.Delete(missing.ConfigPath);
        }
    }
}
