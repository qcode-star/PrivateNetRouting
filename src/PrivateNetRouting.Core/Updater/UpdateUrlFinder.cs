using System.Text.RegularExpressions;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Updater;

public interface IUpdateUrlFinder
{
    /// <summary>Последний адрес пакета из журнала AppX; null — свежего адреса нет.</summary>
    Task<string?> FindLatestPackageUrlAsync(CancellationToken ct);
}

/// <summary>
/// Адрес последнего пакета Claude из журнала AppX: штатный апдейтер, не сумев скачать,
/// оставляет его в Microsoft-Windows-AppXDeploymentServer/Operational примерно раз в час
/// (первые события журнала — самые свежие). Сообщение целиком забирается через Get-WinEvent,
/// разбор адреса — <see cref="PackageUrl.ExtractFromMessage"/>.
/// </summary>
public sealed class PowerShellJournalUrlFinder(UpdaterConfig config, IPowerShellExecutor powershell, FileLog log) : IUpdateUrlFinder
{
    private const string LogName = "Microsoft-Windows-AppXDeploymentServer/Operational";

    public async Task<string?> FindLatestPackageUrlAsync(CancellationToken ct)
    {
        var script =
            "$ev = Get-WinEvent -LogName '" + LogName + "' -MaxEvents " + config.JournalMaxEvents +
            " -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Message -like '*" + config.PackageHost.Replace("'", "''") + "*' } | " +
            "Select-Object -First 1; " +
            "if ($ev) { $ev.Message }";
        var (code, output) = await powershell.RunScriptAsync(script, ct);
        if (code != 0)
        {
            log.Warn($"updater: Get-WinEvent failed (exit {code}): {output}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var url = PackageUrl.ExtractFromMessage(output, config.PackageHost);
        if (url is null)
        {
            log.Warn("updater: a journal event matched the package host but contained no .msix URL");
        }

        return url;
    }
}

/// <summary>Разбор адресов пакетов: извлечение из событий журнала, версия и имя файла из URL.</summary>
public static partial class PackageUrl
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Извлекает адрес пакета из сообщения события. URL в сообщении может быть разорван
    /// переносом строки — поэтому пробелы схлопываются до поиска.
    /// </summary>
    public static string? ExtractFromMessage(string message, string host)
    {
        if (!message.Contains(host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var flat = Whitespace().Replace(message, "");
        var match = Regex.Match(flat, $"https://{Regex.Escape(host)}/[^\"']+\\.msix", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    /// <summary>Версия из пути пакета (/releases/win32/x64/&lt;version&gt;/...); null — путь другой формы.</summary>
    public static string? ParseOfferedVersion(string url)
    {
        var match = Regex.Match(url, @"/releases/win32/x64/([^/]+)/", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Имя файла пакета без строки запроса; пусто, если адрес не указывает на .msix.</summary>
    public static string ParseFileName(string url)
    {
        var name = Path.GetFileName(url.Split('?')[0]);
        return name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ? name : "";
    }

    /// <summary>Ручной адрес пригоден: https и указывает на .msix.</summary>
    public static bool IsValidPackageUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && ParseFileName(url).Length > 0;
}
