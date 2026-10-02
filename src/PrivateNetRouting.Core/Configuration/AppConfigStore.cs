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
    /// Точечное изменение конфига на диске (используется настройками UI, например сменой
    /// языка). Файл перечитывается без раскрытия переменных окружения — чтобы %APPDATA%
    /// в путях не сохранялся раскрытым — мутируется и пишется назад.
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
    /// <summary>Раскрывает переменные окружения в путевых свойствах (%APPDATA% и т.п.).</summary>
    public static void ExpandEnvironmentVariables(this AppConfig config)
    {
        config.Logging.Directory = Environment.ExpandEnvironmentVariables(config.Logging.Directory);
        config.ForwardProxy.ActivityMarkerPath =
            Environment.ExpandEnvironmentVariables(config.ForwardProxy.ActivityMarkerPath);
        config.Chain.StateDir = Environment.ExpandEnvironmentVariables(config.Chain.StateDir);
    }
}
