using System.Diagnostics;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using Microsoft.Win32;

namespace PrivateNetRouting.Core.Launcher;

/// <summary>Режим запуска: обычный или «только проверить/поднять цепочку» (Claude не стартует).</summary>
public sealed record LauncherOptions(bool CheckOnly)
{
    public static LauncherOptions Default { get; } = new(CheckOnly: false);
}

public enum LaunchOutcome
{
    /// <summary>Claude Desktop запущен с флагами прокси.</summary>
    Launched,

    /// <summary>Режим --check-only: цепочка жива, Claude не запускался.</summary>
    CheckOnly,
}

public sealed record LaunchResult(LaunchOutcome Outcome, bool ChainRaised, TimeSpan Elapsed);

/// <summary>
/// Сборка --proxy-bypass-list: фиксированный базовый список (localhost и приватные
/// диапазоны — как в claude-launcher.ps1) плюс домены конфига, каждый в двух формах —
/// в Chromium 'dom.com' матчит только сам домен, а '*.dom.com' — только поддомены.
/// </summary>
public static class ClaudeBypassList
{
    private static readonly string[] BaseParts =
    [
        "<local>", "localhost", "127.0.0.1", "[::1]",
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "*.local",
    ];

    public static string Build(IEnumerable<string> domains)
    {
        var parts = new List<string>(BaseParts);
        foreach (var domain in domains)
        {
            var zone = domain.Trim().TrimStart('.');
            if (zone.Length == 0)
            {
                continue;
            }

            parts.Add(zone);
            parts.Add("*." + zone);
        }

        return string.Join(";", parts);
    }
}

/// <summary>
/// Лончер Claude Desktop (порт claude-launcher.ps1): проверяет цепочку живым
/// CONNECT-roundtrip, при отказе идемпотентно поднимает её (единственное окно UAC —
/// старт демона), затем запускает Claude с --proxy-server/--proxy-bypass-list.
/// Managed-политика egressProxyUrl и уже запущенные копии Claude — только контроль
/// с предупреждением в лог (остановить запуск они не могут, как и в прежней схеме).
/// </summary>
public sealed class ClaudeLauncher(
    AppConfig config,
    IChainSupervisor chain,
    Func<CancellationToken, Task<bool>> healthCheck,
    IProcessFinder processFinder,
    FileLog log,
    Func<string?>? readEgressPolicy = null,
    Func<ProcessStartInfo, Process?>? startProcess = null)
{
    private const string PolicyKey = @"SOFTWARE\Policies\Claude";
    private const string PolicyValue = "egressProxyUrl";

    private readonly Func<string?> _readEgressPolicy = readEgressPolicy ?? ReadEgressPolicyFromRegistry;
    private readonly Func<ProcessStartInfo, Process?> _startProcess = startProcess ?? (psi => Process.Start(psi));

    /// <summary>Лог лончера (заставка предлагает его открыть при сбое).</summary>
    public FileLog Log => log;

    /// <summary>Адрес прокси для --proxy-server и сверки с managed-политикой.</summary>
    public string ProxyUrl => "http://" + config.ForwardProxy.Listen;

    public string BuildBypassList() => ClaudeBypassList.Build(config.Launcher.BypassDomains);

    public async Task<LaunchResult> LaunchAsync(LauncherOptions options, IProgress<ChainStep>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var chainRaised = false;
        if (await healthCheck(ct))
        {
            log.Info($"launcher: proxy {config.ForwardProxy.Listen} responds");
        }
        else
        {
            log.Info("launcher: proxy is not responding — bringing the chain up");
            await chain.EnsureRunningAsync(progress, ct);
            chainRaised = true;
        }

        if (options.CheckOnly)
        {
            log.Info("launcher: check-only, Claude is not started");
            return new LaunchResult(LaunchOutcome.CheckOnly, chainRaised, watch.Elapsed);
        }

        WarnIfPolicyMissing();
        WarnIfClaudeRunningWithoutProxy();

        var info = new ProcessStartInfo(config.Launcher.AliasPath)
        {
            UseShellExecute = true,
        };
        info.ArgumentList.Add($"--proxy-server={ProxyUrl}");
        info.ArgumentList.Add($"--proxy-bypass-list={BuildBypassList()}");
        try
        {
            using var started = _startProcess(info);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            throw new IOException($"failed to start Claude ({config.Launcher.AliasPath}): {ex.Message}", ex);
        }

        log.Info("launcher: Claude started with --proxy-server/--proxy-bypass-list");
        return new LaunchResult(LaunchOutcome.Launched, chainRaised, watch.Elapsed);
    }

    /// <summary>
    /// Страховка от запуска не через лончер: managed-политика действует при любом способе
    /// запуска, флаги — только при нашем. Отсутствие/расхождение политики — предупреждение
    /// в лог (ставит её отдельный модуль Guard, требует прав администратора).
    /// </summary>
    private void WarnIfPolicyMissing()
    {
        string? policy;
        try
        {
            policy = _readEgressPolicy();
        }
        catch (Exception ex)
        {
            log.Warn($"launcher: egress policy read failed: {ex.Message}");
            return;
        }

        if (policy == ProxyUrl)
        {
            log.Info($"launcher: managed policy {PolicyValue} is in place");
        }
        else
        {
            log.Warn($"launcher: managed policy {PolicyValue} is missing or different [{policy ?? ""}] — a start outside the launcher goes direct");
        }
    }

    /// <summary>Уже запущенный Claude без --proxy-server работал бы мимо прокси: предупредить.</summary>
    private void WarnIfClaudeRunningWithoutProxy()
    {
        IReadOnlyList<FoundProcess> processes;
        try
        {
            processes = processFinder.FindByNameContains("claude");
        }
        catch (Exception ex)
        {
            log.Warn($"launcher: process scan failed: {ex.Message}");
            return;
        }

        if (processes.Count == 0)
        {
            return;
        }

        var withProxy = processes.Count(p => p.CommandLine.Contains("--proxy-server=", StringComparison.Ordinal));
        if (withProxy == 0)
        {
            log.Warn($"launcher: Claude is already running ({processes.Count} processes) without --proxy-server — close it and use the launcher again");
        }
        else
        {
            log.Info($"launcher: Claude is already running ({processes.Count} processes) — starting one more instance activates the existing window");
        }
    }

    private static string? ReadEgressPolicyFromRegistry() =>
        Registry.GetValue($"HKEY_LOCAL_MACHINE\\{PolicyKey}", PolicyValue, null) as string;
}
