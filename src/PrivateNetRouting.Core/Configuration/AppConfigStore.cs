using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// Загрузка/сохранение пользовательского конфига. Файл живёт в %APPDATA%, в git не попадает;
/// в репозитории — только шаблон config/appsettings.example.json с плейсхолдерами (правило №1-2 AGENTS.md).
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

    /// <summary>Путь по умолчанию: %APPDATA%\PrivateNetRouting\appsettings.json.</summary>
    public static string GetDefaultConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrivateNetRouting",
            "appsettings.json");

    /// <summary>Загружает конфиг; при отсутствии файла создаёт его из значений по умолчанию.</summary>
    public static AppConfig LoadOrCreate(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            var config = JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions)
                ?? throw new InvalidDataException($"конфиг {path} пуст или повреждён");
            config.ExpandEnvironmentVariables();
            return config;
        }

        var fresh = new AppConfig();
        fresh.ExpandEnvironmentVariables();
        Save(fresh, path);
        return fresh;
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= GetDefaultConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, config, JsonOptions);
    }
}

file static class AppConfigExtensions
{
    /// <summary>Раскрывает переменные окружения в путевых свойствах (%APPDATA% и т.п.).</summary>
    public static void ExpandEnvironmentVariables(this AppConfig config)
    {
        config.Logging.Directory = Environment.ExpandEnvironmentVariables(config.Logging.Directory);
        config.ForwardProxy.ActivityMarkerPath =
            Environment.ExpandEnvironmentVariables(config.ForwardProxy.ActivityMarkerPath);
        config.Chain.StateDir = Environment.ExpandEnvironmentVariables(config.Chain.StateDir);
    }
}
