using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Net.Dns;

public interface IDohEndpointSource
{
    Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct);
}

/// <summary>
/// Reads DoH resolvers from the tailnet config as seen by the sidecar daemon:
/// `tailscale --socket &lt;pipe&gt; debug netmap` carries them as http(s)://…/dns-query URLs —
/// to IP literals or to names. Any tailscale DNS config is honored: a named resolver is
/// resolved once at discovery by the system resolver (resolver names are not part of the
/// AI ban) and the name is kept for the TLS SNI; an IPv6 literal is skipped — the dial is
/// IPv4-only. Changes on the admins' side are picked up automatically. The CLI and the pipe
/// are chain.tailscale.cliPath / .socket — the same ones the chain itself speaks through.
/// </summary>
public sealed partial class TailscaleNetmapDohSource(ChainTailscaleConfig tailscale, FileLog? log = null) : IDohEndpointSource
{
    /// <summary>Hard limit of one netmap call.</summary>
    private static readonly TimeSpan NetmapTimeout = TimeSpan.FromSeconds(40);

    [GeneratedRegex(@"(https?)://(?<host>[A-Za-z0-9._-]+|\[[0-9a-fA-F:]+\])(?::(?<port>\d+))?/dns-query")]
    private static partial Regex DohRouteRegex();

    public async Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct)
    {
        var info = new ProcessStartInfo(tailscale.CliPath)
        {
            ArgumentList = { "--socket", tailscale.Socket, "debug", "netmap" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info)
            ?? throw new IOException($"failed to start {tailscale.CliPath}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(NetmapTimeout);

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        if (process.ExitCode != 0)
        {
            // a dead sidecar pipe: the pool keeps what it has instead of reading "no resolvers"
            throw new IOException($"{tailscale.CliPath} debug netmap exited with code {process.ExitCode}");
        }

        return await ParseEndpointsAsync(stdout, ResolveHostAsync, ct, log);
    }

    /// <summary>
    /// Parsing of the netmap text, separated for tests. A host that is not an IPv4 literal is
    /// resolved by the injected resolver; a resolver that does not resolve is skipped, the rest
    /// of the netmap still counts.
    /// </summary>
    internal static async Task<IReadOnlyList<DohEndpoint>> ParseEndpointsAsync(
        string netmap,
        Func<string, CancellationToken, Task<IPAddress?>> resolveHost,
        CancellationToken ct,
        FileLog? log = null)
    {
        var found = new List<DohEndpoint>();
        foreach (var match in DohRouteRegex().Matches(netmap).Cast<Match>())
        {
            ct.ThrowIfCancellationRequested();
            var secure = match.Groups[1].Value == Uri.UriSchemeHttps;
            var hostGroup = match.Groups["host"].Value;
            var host = hostGroup.StartsWith('[') ? hostGroup.Trim('[', ']') : hostGroup;
            // an omitted port means the scheme default (Uri semantics for the config seeds too)
            var port = match.Groups["port"].Success
                ? int.Parse(match.Groups["port"].Value)
                : secure ? 443 : 80;

            DohEndpoint? endpoint = null;
            if (IPAddress.TryParse(host, out var literal))
            {
                if (literal.AddressFamily == AddressFamily.InterNetwork)
                {
                    endpoint = new DohEndpoint(literal, port, secure);
                }
            }
            else
            {
                try
                {
                    var resolved = await resolveHost(host, ct);
                    if (resolved is not null)
                    {
                        endpoint = new DohEndpoint(resolved, port, secure, HostName: host);
                    }
                    else
                    {
                        log?.Warn($"doh: discovery: resolver host {host} has no IPv4 address, skipped");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Warn($"doh: discovery: resolver host {host} did not resolve, skipped: {ex.Message}");
                }
            }

            if (endpoint is null)
            {
                log?.Debug($"doh: discovery: unsuitable resolver entry skipped: {match.Value}");
                continue;
            }

            if (!found.Contains(endpoint))
            {
                found.Add(endpoint);
            }
        }

        return found;
    }

    private static async Task<IPAddress?> ResolveHostAsync(string host, CancellationToken ct)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    }
}
