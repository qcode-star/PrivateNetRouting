using System.Collections;
using System.Reflection;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Configuration;

/// <summary>
/// One validation pass over a loaded user config, with a per-key message for everything that would
/// otherwise crash the composition much later (a host name where an IP address is parsed, a port
/// out of range, a section or list written as null) or loop and spin (a zero chunk size, a zero
/// poll interval). The endpoints are checked only for the chain mode that uses them, so an unused
/// section of the template (the empty HTTP upstream of a socks5 setup) never blocks the start.
/// </summary>
public static class AppConfigValidator
{
    /// <summary>The problems found, each naming its config key; empty — the config is usable.</summary>
    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        var errors = new List<string>();
        CheckNulls(config, "", errors);
        if (errors.Count > 0)
        {
            // everything below dereferences sections: a null one has to be fixed first
            return errors;
        }

        CheckEndpoints(config, errors);
        CheckTailscaleCommandLine(config, errors);
        CheckTimings(config, errors);
        return errors;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> listing every problem; no-op for a valid config.</summary>
    public static void ThrowIfInvalid(AppConfig config, string source)
    {
        var errors = Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"config {source} is invalid:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", errors)}");
        }
    }

    /// <summary>Every section and list the code dereferences must exist ("ui": null is a crash waiting to happen).</summary>
    private static void CheckNulls(object node, string path, List<string> errors)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType
                || property.PropertyType == typeof(string))
            {
                continue;
            }

            var key = path.Length == 0 ? CamelCase(property.Name) : $"{path}.{CamelCase(property.Name)}";
            var value = property.GetValue(node);
            if (value is null)
            {
                errors.Add($"{key}: must not be null");
            }
            else if (property.PropertyType.Namespace == typeof(AppConfig).Namespace && value is not IEnumerable)
            {
                CheckNulls(value, key, errors);
            }
        }
    }

    private static void CheckEndpoints(AppConfig config, List<string> errors)
    {
        // read by the local proxy and by every client pointed at it — only where the local proxy runs:
        // in the skipped shapes (the http mode, socks5 + forwardProxy.enabled=false) the clients
        // point at the upstream and the value is never read
        if (!AppProxyEndpoint.IsSkipped(config))
        {
            Endpoint("forwardProxy.listen", config.ForwardProxy.Listen, errors);
        }

        switch (config.Chain.EffectiveMode)
        {
            case ChainMode.Socks5:
                Endpoint("chain.socks5.upstream", config.Chain.Socks5.Upstream, errors);
                break;
            case ChainMode.Http:
                Endpoint("chain.http.upstream", config.Chain.Http.Upstream, errors);
                break;
            default:
                Endpoint("chain.tailscale.socksListen", config.Chain.Tailscale.SocksListen, errors);
                Endpoint("chain.tailscale.httpListen", config.Chain.Tailscale.HttpListen, errors);
                break;
        }

        Port("forwardProxy.healthCheckPort", config.ForwardProxy.HealthCheckPort, errors);
    }

    /// <summary>
    /// The daemon and keeper are started through a cmd.exe line built from the config; cmd parses
    /// quotes and metacharacters itself, so these values must be inert before they can reach it.
    /// The section is read only in the tailscale mode — the external modes may leave it blank (A-09).
    /// </summary>
    private static void CheckTailscaleCommandLine(AppConfig config, List<string> errors)
    {
        if (config.Chain.EffectiveMode != ChainMode.Tailscale)
        {
            return;
        }

        var tailscale = config.Chain.Tailscale;
        if (!CommandLineSafety.IsValidPipePath(tailscale.Socket))
        {
            errors.Add($"chain.tailscale.socket: '{tailscale.Socket}' is not a local named-pipe path (expected \\\\.\\pipe\\<name>)");
        }

        AbsolutePath("chain.tailscale.stateDir", tailscale.StateDir, errors);
        AbsolutePath("chain.tailscale.tailscaledPath", tailscale.TailscaledPath, errors);
        AbsolutePath("chain.tailscale.cliPath", tailscale.CliPath, errors);
    }

    private static void AbsolutePath(string key, string value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key}: is empty");
            return;
        }

        // %VAR% is a legitimate spelling of a user path; what matters is the value cmd sees later
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (!CommandLineSafety.IsInert(expanded))
        {
            errors.Add($"{key}: '{value}' contains a cmd.exe metacharacter (one of \" & | < > ^ %) or a line break");
            return;
        }

        if (!Path.IsPathFullyQualified(expanded))
        {
            errors.Add($"{key}: '{value}' is not an absolute path");
        }
    }

    private static void CheckTimings(AppConfig config, List<string> errors)
    {
        Positive("restartGraceSeconds", config.RestartGraceSeconds, errors);
        NotNegative("closeWaitSeconds", config.CloseWaitSeconds, errors);
        NotNegative("killSettleSeconds", config.KillSettleSeconds, errors);
        Positive("watchdog.checkSeconds", config.Watchdog.CheckSeconds, errors);
        Positive("claude.update.chunkBytes", config.Claude.Update.ChunkBytes, errors);
        Positive("claude.update.parallelChunks", config.Claude.Update.ParallelChunks, errors);
        NotNegative("claude.update.chunkRetries", config.Claude.Update.ChunkRetries, errors);
        Positive("claude.update.chunkTimeoutSeconds", config.Claude.Update.ChunkTimeoutSeconds, errors);
        Positive("claude.update.headTimeoutSeconds", config.Claude.Update.HeadTimeoutSeconds, errors);
        Positive("chain.healthWaitSeconds", config.Chain.HealthWaitSeconds, errors);
        Positive("forwardProxy.socksTimeoutSeconds", config.ForwardProxy.SocksTimeoutSeconds, errors);
        Positive("forwardProxy.headerTimeoutSeconds", config.ForwardProxy.HeaderTimeoutSeconds, errors);
        Positive("forwardProxy.dohRefreshSeconds", config.ForwardProxy.DohRefreshSeconds, errors);
        Positive("forwardProxy.dohProbeTimeoutSeconds", config.ForwardProxy.DohProbeTimeoutSeconds, errors);
        Positive("forwardProxy.dohDeadCooldownSeconds", config.ForwardProxy.DohDeadCooldownSeconds, errors);
        Positive("forwardProxy.cacheTtlMinSeconds", config.ForwardProxy.CacheTtlMinSeconds, errors);
        if (config.ForwardProxy.CacheTtlMaxSeconds < config.ForwardProxy.CacheTtlMinSeconds)
        {
            errors.Add(
                $"forwardProxy.cacheTtlMaxSeconds: {config.ForwardProxy.CacheTtlMaxSeconds} is below forwardProxy.cacheTtlMinSeconds ({config.ForwardProxy.CacheTtlMinSeconds})");
        }
        Positive("logging.maxBytes", config.Logging.MaxBytes, errors);
        NotNegative("logging.keep", config.Logging.Keep, errors);
    }

    private static void Endpoint(string key, string value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key}: is empty (expected 'IP:port')");
            return;
        }

        try
        {
            var endpoint = Socks5Client.ParseEndPoint(value);
            if (endpoint.Port is < 1 or > 65535)
            {
                errors.Add($"{key}: port {endpoint.Port} is out of range 1-65535");
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            errors.Add($"{key}: '{value}' has a port out of range 1-65535");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            errors.Add($"{key}: '{value}' is not an 'IP:port' (host names are not resolved here): {ex.Message}");
        }
    }

    private static void Port(string key, int value, List<string> errors)
    {
        if (value is < 1 or > 65535)
        {
            errors.Add($"{key}: {value} is out of range 1-65535");
        }
    }

    private static void Positive(string key, long value, List<string> errors)
    {
        if (value < 1)
        {
            errors.Add($"{key}: {value} must be at least 1");
        }
    }

    private static void NotNegative(string key, long value, List<string> errors)
    {
        if (value < 0)
        {
            errors.Add($"{key}: {value} must not be negative");
        }
    }

    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
