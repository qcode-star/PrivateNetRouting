using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Net.Dns;

public interface IDohEndpointSource
{
    Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct);
}

/// <summary>
/// Дочитывает DoH-маршруты из конфига tailnet, каким его видит sidecar-демон:
/// `tailscale --socket &lt;pipe&gt; debug netmap` содержит все http://ip:port/dns-query.
/// Изменения на стороне админов подхватываются автоматически.
/// </summary>
public sealed partial class TailscaleNetmapDohSource(DohDiscoveryConfig config) : IDohEndpointSource
{
    [GeneratedRegex(@"http://(\d+\.\d+\.\d+\.\d+):(\d+)/dns-query")]
    private static partial Regex DohRouteRegex();

    public async Task<IReadOnlyList<DohEndpoint>> GetEndpointsAsync(CancellationToken ct)
    {
        var info = new ProcessStartInfo(config.CliPath)
        {
            ArgumentList = { "--socket", config.Socket, "debug", "netmap" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info)
            ?? throw new IOException($"не удалось запустить {config.CliPath}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(config.NetmapTimeout);

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        var found = new List<DohEndpoint>();
        foreach (var match in DohRouteRegex().Matches(stdout).Cast<Match>())
        {
            var endpoint = new DohEndpoint(IPAddress.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
            if (!found.Contains(endpoint))
            {
                found.Add(endpoint);
            }
        }

        return found;
    }
}
