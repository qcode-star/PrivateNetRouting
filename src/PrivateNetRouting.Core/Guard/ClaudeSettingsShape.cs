using System.Text.Json.Nodes;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// The part of settings.json of Claude Code that PNR maintains beyond the proxy env: the
/// nonessential-traffic switches (telemetry, error reporting, the feedback paths, the session
/// surveys) and the attribution keys (no Co-Authored-By trailer on commits, no attribution line
/// on pull requests). The manifest is policy, not configuration — every synced file gets exactly
/// these keys; foreign keys and values outside the manifest survive untouched. A maintained value
/// counts as in sync only at its exact expected string, and the status reports every missing or
/// different one by name. An "attribution" that is neither an object nor false is left as written
/// (the owner fixes it, the status reports it) — only the "env" shape is a hard refusal.
/// </summary>
public static class ClaudeSettingsShape
{
    /// <summary>The nonessential-traffic env switches, provisioned together with the proxy env.</summary>
    public static readonly IReadOnlyDictionary<string, string> PrivacyEnv = new Dictionary<string, string>
    {
        ["DISABLE_TELEMETRY"] = "1",
        ["DISABLE_ERROR_REPORTING"] = "1",
        ["DISABLE_BUG_COMMAND"] = "1",
        ["CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY"] = "1",
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
    };

    /// <summary>
    /// Applies everything PNR maintains: the proxy env, the privacy switches,
    /// includeCoAuthoredBy=false and attribution commit/pr hidden. Returns whether the root
    /// changed; a maintained key whose value is not the expected string (a wrong type included)
    /// is replaced.
    /// </summary>
    public static bool Apply(JsonObject root, string proxy, string noProxy)
    {
        if (root["env"] is { } existing && existing is not JsonObject)
        {
            throw new InvalidDataException("settings.json \"env\" is not a JSON object");
        }

        var env = root["env"] as JsonObject;
        var changed = false;
        if (env is null)
        {
            env = [];
            root["env"] = env;
            changed = true;
        }

        changed |= SetText(env, "HTTPS_PROXY", proxy);
        changed |= SetText(env, "HTTP_PROXY", proxy);
        changed |= SetText(env, "NO_PROXY", noProxy);
        foreach (var (key, value) in PrivacyEnv)
        {
            changed |= SetText(env, key, value);
        }

        changed |= ApplyAttribution(root);
        changed |= ApplyNoCoAuthor(root);
        return changed;
    }

    /// <summary>
    /// Names of the maintained values that are missing or different, empty when the file is in
    /// sync. The proxy env is not listed here — its expected values are config-dependent and the
    /// callers compare them with their own wording.
    /// </summary>
    public static IReadOnlyList<string> DriftOf(JsonObject root)
    {
        var drift = new List<string>();
        var env = root["env"] as JsonObject;
        foreach (var (key, value) in PrivacyEnv)
        {
            if (Text(env?[key]) != value)
            {
                drift.Add(key);
            }
        }

        if (root["includeCoAuthoredBy"] is not JsonValue flag || !flag.TryGetValue<bool>(out var include) || include)
        {
            drift.Add("includeCoAuthoredBy");
        }

        switch (root["attribution"])
        {
            case JsonObject attribution:
                if (Text(attribution["commit"]) != "")
                {
                    drift.Add("attribution.commit");
                }

                if (Text(attribution["pr"]) != "")
                {
                    drift.Add("attribution.pr");
                }

                break;
            case JsonValue value when value.TryGetValue<bool>(out var hidden) && !hidden:
                break; // attribution hidden entirely — nothing is missing
            default:
                drift.Add("attribution");
                break;
        }

        return drift;
    }

    /// <summary>String value of a node; a missing or non-string value reads as null (a wrong type is a wrong value).</summary>
    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool SetText(JsonObject env, string key, string value)
    {
        if (Text(env[key]) == value)
        {
            return false;
        }

        env[key] = value;
        return true;
    }

    /// <summary>Commit trailer and PR attribution line hidden; a foreign sub-key survives.</summary>
    private static bool ApplyAttribution(JsonObject root)
    {
        switch (root["attribution"])
        {
            case null:
                root["attribution"] = new JsonObject { ["commit"] = "", ["pr"] = "" };
                return true;
            case JsonObject attribution:
                var changed = false;
                changed |= SetText(attribution, "commit", "");
                changed |= SetText(attribution, "pr", "");
                return changed;
            default:
                // attribution: false (all hidden) or a shape we do not maintain — left as written
                return false;
        }
    }

    private static bool ApplyNoCoAuthor(JsonObject root)
    {
        if (root["includeCoAuthoredBy"] is JsonValue value && value.TryGetValue<bool>(out var include) && !include)
        {
            return false;
        }

        root["includeCoAuthoredBy"] = false;
        return true;
    }
}
