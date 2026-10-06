using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// Loading/saving the user config. The file lives in %APPDATA% and does not go into git;
/// the repository contains only the template config/appsettings.example.json with placeholders (rules #2-3 of AGENTS.md).
/// </summary>
public static class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Default path: %APPDATA%\PrivateNetRouting\appsettings.json.</summary>
    public static string GetDefaultConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrivateNetRouting",
            "appsettings.json");

    /// <summary>Loads the config; if the file is absent, creates it from the default values.</summary>
    public static AppConfig LoadOrCreate(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            var config = Deserialize(stream, path);
            config.ExpandEnvironmentVariables();
            return config;
        }

        var fresh = new AppConfig();
        fresh.ExpandEnvironmentVariables();
        Save(fresh, path);
        return fresh;
    }

    /// <summary>
    /// Point change of the config on disk (used by UI settings, e.g. language
    /// switching). The file is re-read without expanding environment variables — so that %APPDATA%
    /// in paths is not saved expanded — then mutated and written back.
    /// </summary>
    public static void Update(Action<AppConfig> mutate, string? path = null)
    {
        path ??= GetDefaultConfigPath();
        AppConfig config;
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            config = Deserialize(stream, path);
        }
        else
        {
            config = new AppConfig();
        }

        mutate(config);
        Save(config, path);
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= GetDefaultConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, config, JsonOptions);
    }

    private static AppConfig Deserialize(Stream stream, string path) =>
        JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions)
        ?? throw new InvalidDataException($"config {path} is empty or corrupted");
}

file static class AppConfigExtensions
{
    /// <summary>Expands environment variables in path properties (%APPDATA% etc.).</summary>
    public static void ExpandEnvironmentVariables(this AppConfig config)
    {
        config.Logging.Directory = Environment.ExpandEnvironmentVariables(config.Logging.Directory);
        config.ForwardProxy.ActivityMarkerPath =
            Environment.ExpandEnvironmentVariables(config.ForwardProxy.ActivityMarkerPath);
        config.Chain.StateDir = Environment.ExpandEnvironmentVariables(config.Chain.StateDir);
        config.Launcher.AliasPath = Environment.ExpandEnvironmentVariables(config.Launcher.AliasPath);
        config.Browser.Firefox.Path = Environment.ExpandEnvironmentVariables(config.Browser.Firefox.Path);
        config.Browser.Firefox.ProfileDir = Environment.ExpandEnvironmentVariables(config.Browser.Firefox.ProfileDir);
        config.Browser.Chromium.Path = Environment.ExpandEnvironmentVariables(config.Browser.Chromium.Path);
        config.Browser.Chromium.ProfileDir = Environment.ExpandEnvironmentVariables(config.Browser.Chromium.ProfileDir);
        config.Updater.CacheDir = Environment.ExpandEnvironmentVariables(config.Updater.CacheDir);
        config.Updater.PowerShellPath = Environment.ExpandEnvironmentVariables(config.Updater.PowerShellPath);
        config.Guard.HostsFile = Environment.ExpandEnvironmentVariables(config.Guard.HostsFile);
        config.Guard.ClaudeSettingsPath = Environment.ExpandEnvironmentVariables(config.Guard.ClaudeSettingsPath);
    }
}
