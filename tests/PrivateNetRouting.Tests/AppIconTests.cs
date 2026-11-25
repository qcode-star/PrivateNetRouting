using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>
/// The profile icon identity: which variant the enabled apps resolve to, where the shortcut icon
/// files live, and the refresher that re-icons only the desktop shortcuts that start this profile —
/// and only while their icon is still one of ours.
/// </summary>
public class AppIconTests : IDisposable
{
    private const string ExePath = @"C:\Apps\PrivateNetRouting\PrivateNetRouting.App.exe";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"pnr-icons-{Guid.NewGuid():N}.log");
    private readonly string _desktop = Path.Combine(Path.GetTempPath(), $"pnr-icons-desktop-{Guid.NewGuid():N}");
    private readonly FileLog _log;
    private readonly FakeShortcuts _shortcuts = new();

    public AppIconTests()
    {
        _log = new FileLog(_logPath);
        Directory.CreateDirectory(_desktop);
    }

    private static ProfileContext NamedProfile(string name = "example") =>
        AppProfile.Resolve(name);

    private ShortcutIconRefresher CreateRefresher(Func<string, bool>? fileExists = null) =>
        new(_shortcuts, _log, () => _desktop, fileExists ?? (_ => true));

    private string Shortcut(string name, string target, string arguments, string icon)
    {
        var path = Path.Combine(_desktop, name + ".lnk");
        File.WriteAllBytes(path, []);
        _shortcuts.Items[path] = new ShortcutProperties(target, arguments, icon);
        return path;
    }

    // ----- the variant rule -----

    [Theory]
    [InlineData(true, false, AppIconVariant.Claude)]
    [InlineData(false, true, AppIconVariant.Codex)]
    [InlineData(true, true, AppIconVariant.Both)]
    [InlineData(false, false, AppIconVariant.Both)]
    public void TheEnabledAppDecidesTheVariant(bool claude, bool codex, AppIconVariant expected) =>
        Assert.Equal(expected, AppIconVariants.Resolve(claude, codex));

    [Fact]
    public void TheVariantComesFromTheConfig()
    {
        var config = new AppConfig
        {
            Claude = new ClaudeConfig { Enabled = false },
            Codex = new CodexConfig { Enabled = true },
        };

        Assert.Equal(AppIconVariant.Codex, AppIconVariants.Resolve(config));
    }

    [Fact]
    public void TheNeutralVariantHasNoFileAndTheBrandOnesDo()
    {
        Assert.Null(AppIconFiles.PathFor(AppIconVariant.Both));
        Assert.Equal(
            Path.Combine(AppIconFiles.Directory, "app-claude.ico"),
            AppIconFiles.PathFor(AppIconVariant.Claude));
        Assert.Equal(
            Path.Combine(AppIconFiles.Directory, "app-codex.ico"),
            AppIconFiles.PathFor(AppIconVariant.Codex));
    }

    // ----- the shortcut refresher -----

    [Fact]
    public void TheProfileShortcutGetsTheBrandIcon()
    {
        var shortcut = Shortcut("PNR Codex", ExePath, "--profile example", ExePath + ",0");

        var changed = CreateRefresher().Apply(ExePath, NamedProfile(), AppIconVariant.Codex);

        Assert.Equal([shortcut], changed);
        Assert.Equal($"{AppIconFiles.PathFor(AppIconVariant.Codex)},0", _shortcuts.Items[shortcut].IconLocation);
    }

    [Fact]
    public void ShortcutsOfOtherProfilesAndTargetsAreLeftAlone()
    {
        var otherProfile = Shortcut("PNR Claude", ExePath, "--profile claude", ExePath + ",0");
        var otherTarget = Shortcut("Other", @"C:\Apps\Something\else.exe", "--profile example", "");
        var defaultProfile = Shortcut("PNR", ExePath, "", ExePath + ",0");

        var changed = CreateRefresher().Apply(ExePath, NamedProfile(), AppIconVariant.Codex);

        Assert.Empty(changed);
        Assert.Equal(ExePath + ",0", _shortcuts.Items[otherProfile].IconLocation);
        Assert.Equal("", _shortcuts.Items[otherTarget].IconLocation);
        Assert.Equal(ExePath + ",0", _shortcuts.Items[defaultProfile].IconLocation);
    }

    [Fact]
    public void TheDefaultProfileTakesOnlyShortcutsWithoutTheOption()
    {
        var plain = Shortcut("PNR", ExePath, "--launch", ExePath + ",0");
        var named = Shortcut("PNR Claude", ExePath, "--profile claude", ExePath + ",0");

        var changed = CreateRefresher().Apply(ExePath, ProfileContext.Default, AppIconVariant.Claude);

        Assert.Equal([plain], changed);
        Assert.Equal(ExePath + ",0", _shortcuts.Items[named].IconLocation);
    }

    [Theory]
    [InlineData("--profile=example")]
    [InlineData("--launch --profile example")]
    [InlineData("--profile \"example\"")]
    [InlineData("--config \"C:\\a b\\appsettings.json\" --profile example")]
    public void TheProfileOptionIsFoundInAnyForm(string arguments)
    {
        var shortcut = Shortcut("PNR Codex", ExePath, arguments, ExePath + ",0");

        var changed = CreateRefresher().Apply(ExePath, NamedProfile(), AppIconVariant.Codex);

        Assert.Equal([shortcut], changed);
    }

    [Fact]
    public void AnIconTheOwnerChoseIsNeverReplaced()
    {
        var shortcut = Shortcut("PNR Codex", ExePath, "--profile example", @"C:\Windows\System32\shell32.dll,3");

        var changed = CreateRefresher().Apply(ExePath, NamedProfile(), AppIconVariant.Codex);

        Assert.Empty(changed);
        Assert.Equal(@"C:\Windows\System32\shell32.dll,3", _shortcuts.Items[shortcut].IconLocation);
    }

    [Fact]
    public void TheNeutralVariantPutsTheExeIconBack()
    {
        var shortcut = Shortcut(
            "PNR Claude",
            ExePath,
            "--profile claude",
            $"{AppIconFiles.PathFor(AppIconVariant.Claude)},0");

        var changed = CreateRefresher().Apply(ExePath, NamedProfile("claude"), AppIconVariant.Both);

        Assert.Equal([shortcut], changed);
        Assert.Equal(ExePath + ",0", _shortcuts.Items[shortcut].IconLocation);
    }

    [Fact]
    public void AnAlreadyRightIconIsNotWrittenAgain()
    {
        Shortcut("PNR Codex", ExePath, "--profile example", $"{AppIconFiles.PathFor(AppIconVariant.Codex)},0");

        var changed = CreateRefresher().Apply(ExePath, NamedProfile(), AppIconVariant.Codex);

        Assert.Empty(changed);
        Assert.Empty(_shortcuts.Writes);
    }

    [Fact]
    public void AnEmptyIconLocationIsTheExeIcon()
    {
        Shortcut("PNR", ExePath, "", "");

        var changed = CreateRefresher().Apply(ExePath, ProfileContext.Default, AppIconVariant.Both);

        Assert.Empty(changed);
        Assert.Empty(_shortcuts.Writes);
    }

    [Fact]
    public void AMissingBrandIconLeavesTheShortcutsAlone()
    {
        var shortcut = Shortcut("PNR Codex", ExePath, "--profile example", ExePath + ",0");

        var changed = CreateRefresher(_ => false).Apply(ExePath, NamedProfile(), AppIconVariant.Codex);

        Assert.Empty(changed);
        Assert.Empty(_shortcuts.Writes);
        Assert.Equal(ExePath + ",0", _shortcuts.Items[shortcut].IconLocation);
    }

    private sealed class FakeShortcuts : IShortcutStore
    {
        public Dictionary<string, ShortcutProperties> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string ShortcutPath, string IconLocation)> Writes { get; } = [];

        public ShortcutProperties? ReadShortcut(string shortcutPath) =>
            Items.TryGetValue(shortcutPath, out var properties) ? properties : null;

        public void SetShortcutIcon(string shortcutPath, string iconLocation)
        {
            Writes.Add((shortcutPath, iconLocation));
            Items[shortcutPath] = Items[shortcutPath] with { IconLocation = iconLocation };
        }
    }

    public void Dispose()
    {
        _log.Dispose();
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }

        if (Directory.Exists(_desktop))
        {
            Directory.Delete(_desktop, recursive: true);
        }
    }
}
