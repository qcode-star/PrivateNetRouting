using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Guard;

/// <summary>Ожидаемые значения env для settings.json (Windows и WSL строятся одинаково, кроме адреса прокси).</summary>
public static class ClaudeEnvValues
{
    public static string WindowsProxyUrl(ForwardProxyConfig fp) => "http://" + fp.Listen;

    /// <summary>Адрес прокси для WSL: IP виндовой стороны WSL NAT (vEthernet (WSL...)) — не 127.0.0.1.</summary>
    public static string? WslProxyUrl(System.Net.IPAddress? gateway, ForwardProxyConfig fp) =>
        gateway is null ? null : $"http://{gateway}:{Port(fp)}";

    public static int Port(ForwardProxyConfig fp) => Net.Socks.Socks5Client.ParseEndPoint(fp.Listen).Port;

    /// <summary>
    /// NO_PROXY (стиль curl): базовые записи + каждая зона в двух формах — «zone» и «.zone»
    /// (поддомены). Зоны — из launcher.bypassDomains, как в прежней схеме из NRPT-списка.
    /// </summary>
    public static string BuildNoProxy(IEnumerable<string> bypassDomains) =>
        string.Join(",", NoProxyParts(bypassDomains));

    public static IReadOnlyList<string> NoProxyParts(IEnumerable<string> bypassDomains)
    {
        var parts = new List<string> { "localhost", "127.0.0.1", ".local" };
        foreach (var domain in bypassDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            parts.Add(domain);
            parts.Add("." + domain);
        }

        return parts;
    }
}

/// <summary>
/// env в settings.json Claude Code, Windows (порт sync-claude-noproxy.ps1): движок Claude Code
/// (CLI и встроенный в Desktop) применяет env и к сессиям, и к командам в них. Существующие
/// ключи (env и корень) сохраняются, перед записью делается бэкап с меткой времени.
/// Прав не требует.
/// </summary>
public sealed class ClaudeSettingsGuard(AppConfig config)
{
    public ClaudeSettingsStatus Read()
    {
        var expectedProxy = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy);
        var expectedNoProxy = ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains);
        if (!File.Exists(config.Guard.ClaudeSettingsPath))
        {
            return new ClaudeSettingsStatus(GuardState.Off, null, null, expectedProxy, expectedNoProxy, FileMissing: true);
        }

        try
        {
            var env = ReadEnv();
            var proxy = env?["HTTPS_PROXY"]?.GetValue<string>();
            var noProxy = env?["NO_PROXY"]?.GetValue<string>();
            var proxyOk = proxy == expectedProxy && env?["HTTP_PROXY"]?.GetValue<string>() == expectedProxy;
            var noProxyOk = noProxy == expectedNoProxy;
            var state = proxyOk && noProxyOk ? GuardState.On : GuardState.Partial;
            return new ClaudeSettingsStatus(state, proxy, noProxy, expectedProxy, expectedNoProxy, FileMissing: false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ClaudeSettingsStatus(GuardState.Unknown, null, null, expectedProxy, expectedNoProxy, FileMissing: false)
            {
                Detail = ex.Message,
            };
        }
    }

    /// <summary>Записывает env (HTTPS_PROXY/HTTP_PROXY/NO_PROXY); возвращает путь бэкапа (null — файла не было).</summary>
    public string? Sync()
    {
        var path = config.Guard.ClaudeSettingsPath;
        var backup = Backup(path);
        var root = ReadRootOrEmpty(path);
        var env = root["env"] as JsonObject ?? [];
        env["HTTPS_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy);
        env["HTTP_PROXY"] = ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy);
        env["NO_PROXY"] = ClaudeEnvValues.BuildNoProxy(config.Launcher.BypassDomains);
        root["env"] = env;
        Write(path, root);
        return backup;
    }

    internal static JsonObject ReadRootOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonNode.Parse(text, documentOptions: JsonDocOptions) as JsonObject
            ?? throw new InvalidDataException($"settings.json is not a JSON object: {path}");
    }

    internal static JsonObject? ReadEnvOf(JsonObject root) => root["env"] as JsonObject;

    private JsonObject? ReadEnv() => File.Exists(config.Guard.ClaudeSettingsPath)
        ? ReadEnvOf(ReadRootOrEmpty(config.Guard.ClaudeSettingsPath))
        : null;

    internal static string? Backup(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var backupPath = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }

    private static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions JsonDocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
}
