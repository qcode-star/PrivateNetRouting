using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>
/// Режимы запуска: обычный, «только проверить/поднять цепочку» и «подготовить профиль,
/// браузер не открывать» (порт -PrepareOnly browser-proxy.ps1). Url и приватное окно
/// перекрывают значения конфига (флаг CLI — поверх browser.url/browser.private).
/// </summary>
public sealed record BrowserLauncherOptions(bool CheckOnly = false, bool PrepareOnly = false, bool Private = false, string? Url = null)
{
    public static BrowserLauncherOptions Default { get; } = new();
}

public enum BrowserLaunchOutcome
{
    /// <summary>Браузер запущен с изолированным профилем и прокси приложения.</summary>
    Launched,

    /// <summary>Режим --check-only: цепочка жива, браузер не запускался.</summary>
    CheckOnly,

    /// <summary>Режим --prepare-only: профиль подготовлен (Firefox — user.js), браузер не запускался.</summary>
    Prepared,
}

public sealed record BrowserLaunchResult(BrowserLaunchOutcome Outcome, bool ChainRaised, TimeSpan Elapsed);

/// <summary>
/// Списки обхода прокси для браузера. Домены зон — общие с лончером Claude
/// (launcher.bypassDomains), отдельно — домены верхнего уровня, которые ходят мимо
/// прокси (*.ru и соседи: локальные сайты не должны уходить в зарубежный выход).
/// </summary>
public static class BrowserBypassList
{
    /// <summary>
    /// Chromium-совместимый список (--proxy-bypass-list, разделитель «;», CIDR поддерживаются):
    /// базовый список Claude (localhost/приватные диапазоны/подсеть tailnet) + домены обеими
    /// формами + *.tld.
    /// </summary>
    public static string BuildChromium(IEnumerable<string> domains, IEnumerable<string> tlds)
    {
        var parts = new List<string> { ClaudeBypassList.Build(domains) };
        parts.AddRange(TrimTlds(tlds).Select(tld => "*." + tld));
        return string.Join(";", parts);
    }

    /// <summary>
    /// Firefox network.proxy.no_proxies_on (разделитель «,», CIDR НЕ поддерживаются — только
    /// имена): каждый домен зон в двух формах — bare и с ведущей точкой (поддомены) + *.tld.
    /// </summary>
    public static string BuildFirefoxNoProxies(IEnumerable<string> domains, IEnumerable<string> tlds)
    {
        var parts = new List<string> { "localhost", "127.0.0.1", "::1", "*.local" };
        foreach (var domain in domains)
        {
            var zone = domain.Trim().TrimStart('.');
            if (zone.Length == 0)
            {
                continue;
            }

            parts.Add(zone);
            parts.Add("." + zone);
        }

        parts.AddRange(TrimTlds(tlds).Select(tld => "*." + tld));
        return string.Join(", ", parts);
    }

    private static IEnumerable<string> TrimTlds(IEnumerable<string> tlds) =>
        tlds
            .Select(tld => tld.Trim().TrimStart('.').TrimStart('*').TrimStart('.'))
            .Where(tld => tld.Length > 0);
}

/// <summary>user.js изолированного профиля Firefox (порт browser-proxy.ps1): перезаписывается при каждом запуске.</summary>
public static class FirefoxUserJs
{
    public static string Render(string proxyHost, int proxyPort, string noProxies) => $"""
        // Managed by PrivateNetRouting — rewritten on every launch: proxy address and bypass list stay fresh.
        user_pref("network.proxy.type", 1);
        user_pref("network.proxy.http", "{proxyHost}");
        user_pref("network.proxy.http_port", {proxyPort});
        user_pref("network.proxy.ssl", "{proxyHost}");
        user_pref("network.proxy.ssl_port", {proxyPort});
        user_pref("network.proxy.share_proxy_settings", true);
        user_pref("network.proxy.failover_direct", false);
        user_pref("network.proxy.no_proxies_on", "{noProxies}");
        user_pref("network.trr.mode", 5);
        user_pref("browser.shell.checkDefaultBrowser", false);
        user_pref("datareporting.policy.dataSubmissionEnabled", false);
        """;
}

/// <summary>
/// Лончер браузера с изолированным профилем (порт browser-proxy.ps1): проверяет цепочку живым
/// CONNECT-roundtrip, при отказе идемпотентно поднимает её (сплэш/прогресс — как у лончера
/// Claude), затем запускает браузер с прокси приложения и без системных настроек: Firefox —
/// отдельный профиль с user.js и --new-instance (иначе URL уйдёт в повседневный экземпляр без
/// прокси), хромоподобный — --proxy-server/--user-data-dir (отдельный каталог обязателен:
/// уже запущенный экземпляр игнорирует флаги командной строки).
/// </summary>
public sealed class BrowserLauncher(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IProcessFinder processFinder,
    FileLog log,
    Func<string, bool>? fileExists = null,
    Func<ProcessStartInfo, Process?>? startProcess = null)
{
    /// <summary>Откат, если путь конфига не найден: второй стандартный каталог семейства (как в browser-proxy.ps1).</summary>
    private static readonly string FirefoxFallbackPath = @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe";

    private static readonly string ChromiumFallbackPath = @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe";

    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? (psi => Process.Start(psi));

    /// <summary>Лог лончера браузера (заставка предлагает его открыть при сбое).</summary>
    public FileLog Log => log;

    /// <summary>Адрес прокси для --proxy-server (Chromium) и user.js (Firefox).</summary>
    public string ProxyUrl => "http://" + config.ForwardProxy.Listen;

    public string BuildChromiumBypassList() =>
        BrowserBypassList.BuildChromium(config.Launcher.BypassDomains, config.Browser.DirectTlds);

    public string BuildFirefoxNoProxies() =>
        BrowserBypassList.BuildFirefoxNoProxies(config.Launcher.BypassDomains, config.Browser.DirectTlds);

    public async Task<BrowserLaunchResult> LaunchAsync(BrowserLauncherOptions options, IProgress<ChainStep>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var chainRaised = false;
        if (await healthCheck(ct))
        {
            log.Info($"browser: proxy {config.ForwardProxy.Listen} responds");
        }
        else
        {
            log.Info("browser: proxy is not responding — bringing the chain up");
            await chain.EnsureRunningAsync(progress, ct);
            chainRaised = true;
        }

        if (options.CheckOnly)
        {
            log.Info("browser: check-only, browser is not started");
            return new BrowserLaunchResult(BrowserLaunchOutcome.CheckOnly, chainRaised, watch.Elapsed);
        }

        var kind = config.Browser.EffectiveType;
        var exe = ResolveExecutable(kind);
        var url = string.IsNullOrWhiteSpace(options.Url) ? config.Browser.Url : options.Url!;
        var privateWindow = options.Private || config.Browser.Private;

        if (kind == BrowserKind.Firefox)
        {
            // user.js перезаписывается при каждом запуске — адрес прокси и список обхода всегда свежие
            WriteUserJs();
            if (options.PrepareOnly)
            {
                log.Info("browser: prepare-only, user.js written, browser is not started");
                return new BrowserLaunchResult(BrowserLaunchOutcome.Prepared, chainRaised, watch.Elapsed);
            }

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            };
            info.ArgumentList.Add("-profile");
            info.ArgumentList.Add(config.Browser.Firefox.ProfileDir);
            info.ArgumentList.Add("--new-instance");
            if (privateWindow)
            {
                info.ArgumentList.Add("-private-window");
            }

            info.ArgumentList.Add(url);
            Start(info, exe);
            log.Info($"browser: firefox started with -profile/--new-instance (user.js proxy {config.ForwardProxy.Listen})");
        }
        else
        {
            if (options.PrepareOnly)
            {
                log.Info("browser: prepare-only, chromium needs no profile preparation");
                return new BrowserLaunchResult(BrowserLaunchOutcome.Prepared, chainRaised, watch.Elapsed);
            }

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            };
            info.ArgumentList.Add($"--proxy-server={ProxyUrl}");
            info.ArgumentList.Add($"--proxy-bypass-list={BuildChromiumBypassList()}");
            info.ArgumentList.Add($"--user-data-dir={config.Browser.Chromium.ProfileDir}");
            info.ArgumentList.Add("--no-first-run");
            info.ArgumentList.Add("--no-default-browser-check");
            if (privateWindow)
            {
                info.ArgumentList.Add("--incognito");
            }

            info.ArgumentList.Add(url);
            Start(info, exe);
            log.Info("browser: chromium started with --proxy-server/--user-data-dir");
        }

        return new BrowserLaunchResult(BrowserLaunchOutcome.Launched, chainRaised, watch.Elapsed);
    }

    /// <summary>
    /// Запущен ли уже наш экземпляр браузера (процесс с нашим профилем в командной строке).
    /// Повторный запуск Firefox на том же профиле с --new-instance упирается в блокировку
    /// профиля — вместо этого показывается окно приложения (как ярлыковый режим Claude).
    /// </summary>
    public bool IsOurBrowserRunning()
    {
        var kind = config.Browser.EffectiveType;
        var exeName = Path.GetFileNameWithoutExtension(
            kind == BrowserKind.Firefox ? config.Browser.Firefox.Path : config.Browser.Chromium.Path);
        var profileDir = kind == BrowserKind.Firefox ? config.Browser.Firefox.ProfileDir : config.Browser.Chromium.ProfileDir;
        try
        {
            return processFinder
                .FindByNameContains(exeName)
                .Any(p => p.CommandLine.Contains(profileDir, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            log.Warn($"browser: process scan failed: {ex.Message}");
            return false;
        }
    }

    private string ResolveExecutable(BrowserKind kind)
    {
        var primary = kind == BrowserKind.Firefox ? config.Browser.Firefox.Path : config.Browser.Chromium.Path;
        var fallback = Environment.ExpandEnvironmentVariables(
            kind == BrowserKind.Firefox ? FirefoxFallbackPath : ChromiumFallbackPath);
        if (_fileExists(primary))
        {
            return primary;
        }

        if (_fileExists(fallback))
        {
            log.Info($"browser: configured executable not found, using fallback {fallback}");
            return fallback;
        }

        throw new IOException($"browser executable not found ({primary}; fallback {fallback})");
    }

    private void WriteUserJs()
    {
        var endpoint = Socks5Client.ParseEndPoint(config.ForwardProxy.Listen);
        var dir = config.Browser.Firefox.ProfileDir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "user.js"),
            FirefoxUserJs.Render(endpoint.Address.ToString(), endpoint.Port, BuildFirefoxNoProxies()));
        log.Info($"browser: user.js updated (proxy {config.ForwardProxy.Listen}, bypass list rewritten)");
    }

    private void Start(ProcessStartInfo info, string exe)
    {
        try
        {
            using var started = _startProcess(info);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            throw new IOException($"failed to start the browser ({exe}): {ex.Message}", ex);
        }
    }
}
