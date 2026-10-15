using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // the file is read by people: non-ASCII text and <>&+ stay as written, not \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions RawDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
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
            // per-key messages now, instead of a FormatException from deep inside the composition
            AppConfigValidator.ThrowIfInvalid(config, path);
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
    /// Point change of the config on disk (used by UI settings, e.g. language switching). Only
    /// what the mutation actually changed is written: the file is read as raw JSON, the typed
    /// model is compared before and after <paramref name="mutate"/>, and the differing values are
    /// patched into the raw tree. Unknown keys, keys the user left out (new defaults of later
    /// releases keep applying) and the key order survive; a mutation that changes nothing does
    /// not touch the file at all. Environment variables are not expanded on this path, so
    /// %APPDATA% in paths is never saved expanded.
    /// </summary>
    public static void Update(Action<AppConfig> mutate, string? path = null)
    {
        path ??= GetDefaultConfigPath();
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            mutate(fresh);
            Save(fresh, path);
            return;
        }

        var text = ReadShared(path);
        var raw = JsonNode.Parse(text, documentOptions: RawDocumentOptions) as JsonObject
            ?? throw new InvalidDataException($"config {path} is empty or corrupted");
        var config = Deserialize(text, path);
        var before = (JsonObject)JsonSerializer.SerializeToNode(config, JsonOptions)!;
        mutate(config);
        var after = (JsonObject)JsonSerializer.SerializeToNode(config, JsonOptions)!;
        if (!Patch(raw, before, after))
        {
            return;
        }

        WriteAtomic(path, raw.ToJsonString(JsonOptions));
    }

    /// <summary>Applies the differences between two serializations of the model onto the raw file tree.</summary>
    private static bool Patch(JsonObject raw, JsonObject before, JsonObject after)
    {
        var changed = false;
        foreach (var (key, afterValue) in after.ToList())
        {
            before.TryGetPropertyValue(key, out var beforeValue);
            if (beforeValue is not null && JsonNode.DeepEquals(beforeValue, afterValue))
            {
                continue;
            }

            var rawKey = FindKey(raw, key) ?? key;
            if (beforeValue is JsonObject beforeObject && afterValue is JsonObject afterObject
                && (raw[rawKey] is null || raw[rawKey] is JsonObject))
            {
                var target = raw[rawKey] as JsonObject;
                var created = target is null;
                target ??= [];
                if (Patch(target, beforeObject, afterObject))
                {
                    if (created)
                    {
                        raw[rawKey] = target;
                    }

                    changed = true;
                }

                continue;
            }

            raw[rawKey] = afterValue?.DeepClone();
            changed = true;
        }

        foreach (var key in before.Select(pair => pair.Key).Where(key => !after.ContainsKey(key)).ToList())
        {
            if (FindKey(raw, key) is { } rawKey)
            {
                raw.Remove(rawKey);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The spelling of a key in the raw file (property names are matched case-insensitively on load).</summary>
    private static string? FindKey(JsonObject raw, string key) =>
        raw.Select(pair => pair.Key).FirstOrDefault(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
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
    /// launchers, the guard, the updater) sees the fresh values; the watchdog section is copied
    /// into the existing object, because the idle stopper supervisor and the service hold that
    /// very instance. The forwardProxy and chain sections are NOT replaced: the running proxy,
    /// the chain and every endpoint the launchers and guards derive (AppProxyEndpoint, the chain
    /// mode) stay one consistent snapshot until the application restarts. Returns true when the
    /// file holds different values for those two sections, i.e. a restart is needed to apply them.
    /// </summary>
    public static bool ReloadInto(AppConfig target, string? path = null)
    {
        var fresh = LoadOrCreate(path);
        var restartRequired = SectionDiffers(target.ForwardProxy, fresh.ForwardProxy)
            || SectionDiffers(target.Chain, fresh.Chain);
        CopyProperties(fresh.Watchdog, target.Watchdog);
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
        target.Wsl = fresh.Wsl;
        target.Browser = fresh.Browser;
        target.Guard = fresh.Guard;
        return restartRequired;
    }

    private static bool SectionDiffers<T>(T current, T fresh) =>
        JsonSerializer.Serialize(current, JsonOptions) != JsonSerializer.Serialize(fresh, JsonOptions);

    /// <summary>Copies every public settable property of a flat section into an existing instance.</summary>
    private static void CopyProperties<T>(T from, T to) where T : class
    {
        foreach (var property in typeof(T).GetProperties())
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(to, property.GetValue(from));
            }
        }
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
