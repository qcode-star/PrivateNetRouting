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
            var config = Deserialize(ReadShared(path), path);
            config.ExpandEnvironmentVariables();
            return config;
        }

        // saved first: the file keeps %VAR% as written (no user name leaks into it), the
        // in-memory copy is expanded afterwards
        var fresh = new AppConfig();
        Save(fresh, path);
        fresh.ExpandEnvironmentVariables();
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
            config = Deserialize(ReadShared(path), path);
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
        WriteAtomic(path, JsonSerializer.Serialize(config, JsonOptions));
    }

    /// <summary>
    /// The file is replaced in one step (a temp file next to it, then a move over it): a worker
    /// starting during the write reads the old or the new content, never an empty or half-written
    /// file, and a crash mid-write leaves the previous config in place.
    /// </summary>
    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temp, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // the temp file is leftover garbage, not the config
            }

            throw;
        }
    }

    /// <summary>
    /// Reads the file for deserialization. A writer or a scanner may hold it for a moment (the
    /// replacement above is atomic, but an editor or an antivirus can still lock it), so a
    /// sharing violation is retried briefly before it surfaces.
    /// </summary>
    private static string ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static AppConfig Deserialize(string json, string path) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
        ?? throw new InvalidDataException($"config {path} is empty or corrupted");

    /// <summary>
    /// Re-reads the user file into an existing config object (the chain card "Reload" button):
    /// the sections are replaced wholesale, so everything that holds the root AppConfig (the
    /// launchers, the guard, the updater, the next idle stopper start) sees the fresh values.
    /// Values already snapshotted by running components (the proxy and the chain listeners)
    /// apply after the application restart.
    /// </summary>
    public static void ReloadInto(AppConfig target, string? path = null)
    {
        var fresh = LoadOrCreate(path);
        target.BypassDomains = fresh.BypassDomains;
        target.DirectTlds = fresh.DirectTlds;
        target.RestartGraceSeconds = fresh.RestartGraceSeconds;
        target.CloseWaitSeconds = fresh.CloseWaitSeconds;
        target.KillSettleSeconds = fresh.KillSettleSeconds;
        target.PowerShellPath = fresh.PowerShellPath;
        target.Ui = fresh.Ui;
        target.Logging = fresh.Logging;
        target.Claude = fresh.Claude;
        target.Codex = fresh.Codex;
        target.ForwardProxy = fresh.ForwardProxy;
        target.Chain = fresh.Chain;
        target.Wsl = fresh.Wsl;
        target.Watchdog = fresh.Watchdog;
        target.Browser = fresh.Browser;
        target.Guard = fresh.Guard;
    }
}

file static class AppConfigExtensions
{
    /// <summary>Expands environment variables in path properties (%APPDATA% etc.).</summary>
    public static void ExpandEnvironmentVariables(this AppConfig config)
    {
        config.Logging.Directory = Environment.ExpandEnvironmentVariables(config.Logging.Directory);
        config.PowerShellPath = Environment.ExpandEnvironmentVariables(config.PowerShellPath);
        config.ForwardProxy.ActivityMarkerPath =
            Environment.ExpandEnvironmentVariables(config.ForwardProxy.ActivityMarkerPath);
        config.Chain.Tailscale.StateDir = Environment.ExpandEnvironmentVariables(config.Chain.Tailscale.StateDir);
        config.Claude.AliasPath = Environment.ExpandEnvironmentVariables(config.Claude.AliasPath);
        config.Claude.SettingsPath = Environment.ExpandEnvironmentVariables(config.Claude.SettingsPath);
        config.Claude.Update.CacheDir = Environment.ExpandEnvironmentVariables(config.Claude.Update.CacheDir);
        config.Codex.SettingsPath = Environment.ExpandEnvironmentVariables(config.Codex.SettingsPath);
        config.Codex.EnvPath = Environment.ExpandEnvironmentVariables(config.Codex.EnvPath);
        config.Browser.ExecutablePath = Environment.ExpandEnvironmentVariables(config.Browser.ExecutablePath);
        config.Browser.ProfileDir = Environment.ExpandEnvironmentVariables(config.Browser.ProfileDir);
        config.Browser.ShortcutPath = Environment.ExpandEnvironmentVariables(config.Browser.ShortcutPath);
        config.Guard.HostsFile = Environment.ExpandEnvironmentVariables(config.Guard.HostsFile);
    }
}
