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
    /// what the mutation actually changed is written: the typed model is compared before and
    /// after <paramref name="mutate"/>, and the differing values are edited into the file text
    /// surgically (JsonTextPatch) — comments, formatting, unknown keys, keys the user left out
    /// (new defaults of later releases keep applying) and the key order survive; a mutation that
    /// changes nothing does not touch the file at all. Should the surgical edit fail (or not
    /// read back as the intended model), the raw tree is patched and rewritten instead — the
    /// values survive, the comments do not — and <paramref name="log"/> says why. Environment
    /// variables are not expanded on this path, so %APPDATA% in paths is never saved expanded.
    /// </summary>
    public static void Update(Action<AppConfig> mutate, string? path = null, Action<string>? log = null)
    {
        path ??= GetDefaultConfigPath();
        lock (WriteGate)
        {
            UpdateLocked(mutate, path, log ?? (message => System.Diagnostics.Trace.TraceWarning(message)));
        }
    }

    private static void UpdateLocked(Action<AppConfig> mutate, string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            mutate(fresh);
            Save(fresh, path);
            return;
        }

        var text = ReadShared(path);
        var config = Deserialize(text, path);
        var before = (JsonObject)JsonSerializer.SerializeToNode(config, JsonOptions)!;
        mutate(config);
        var after = (JsonObject)JsonSerializer.SerializeToNode(config, JsonOptions)!;
        var edits = new List<JsonTextPatch.Edit>();
        Diff(before, after, [], edits);
        if (edits.Count == 0)
        {
            return;
        }

        string? patched;
        try
        {
            patched = JsonTextPatch.Apply(text, edits);
            var readBack = (JsonObject)JsonSerializer.SerializeToNode(Deserialize(patched, path), JsonOptions)!;
            if (!JsonNode.DeepEquals(readBack, after))
            {
                log($"config: the in-place edit of {path} does not read back as the intended values — rewriting the file (its comments are lost)");
                patched = null;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            log($"config: the in-place edit of {path} failed, rewriting the file (its comments are lost): {ex.Message}");
            patched = null;
        }

        if (patched is null)
        {
            var raw = JsonNode.Parse(text, documentOptions: RawDocumentOptions) as JsonObject
                ?? throw new InvalidDataException($"config {path} is empty or corrupted");
            if (!Patch(raw, before, after))
            {
                return;
            }

            patched = raw.ToJsonString(JsonOptions);
        }

        WriteAtomic(path, patched);
    }

    /// <summary>The changed leaves between two serializations of the model, as key paths; a removed key becomes null.</summary>
    private static void Diff(JsonObject before, JsonObject after, List<string> prefix, List<JsonTextPatch.Edit> edits)
    {
        foreach (var (key, afterValue) in after)
        {
            before.TryGetPropertyValue(key, out var beforeValue);
            if (beforeValue is not null && JsonNode.DeepEquals(beforeValue, afterValue))
            {
                continue;
            }

            List<string> path = [.. prefix, key];
            if (beforeValue is JsonObject beforeObject && afterValue is JsonObject afterObject)
            {
                Diff(beforeObject, afterObject, path, edits);
                continue;
            }

            edits.Add(new JsonTextPatch.Edit(path, afterValue?.DeepClone()));
        }

        foreach (var (key, _) in before)
        {
            if (!after.ContainsKey(key))
            {
                edits.Add(new JsonTextPatch.Edit([.. prefix, key], null));
            }
        }
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

    /// <summary>Serializes the writes of this process (Update is a read-modify-write: two at once would lose one change).</summary>
    private static readonly object WriteGate = new();

    /// <summary>
    /// The file is replaced in one step (a temp file next to it, then a replace over it): a worker
    /// starting during the write reads the old or the new content, never an empty or half-written
    /// file, and a crash mid-write leaves the previous config in place. A symlinked config is
    /// written through to its final target (the link stays a link), and an existing target is
    /// swapped by File.Replace, which keeps its ACL and attributes; the temp name is unique per
    /// write, so concurrent writers never share one.
    /// </summary>
    internal static void WriteAtomic(string path, string content)
    {
        lock (WriteGate)
        {
            WriteAtomicLocked(path, content);
        }
    }

    /// <summary>The file a write lands in: the final target of a symlink, the path itself otherwise.</summary>
    internal static string ResolveWriteTarget(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full; // a link loop or an unreadable reparse point: write the path itself
        }
    }

    private static void WriteAtomicLocked(string path, string content)
    {
        var target = ResolveWriteTarget(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(target))
            {
                File.Replace(temp, target, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
