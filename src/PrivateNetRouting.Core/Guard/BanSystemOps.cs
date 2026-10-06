using System.Text.Json;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Core.Guard;

public sealed record BanLayersState(int NrptRules, IReadOnlyList<string> NrptNamespaces, int FirewallRules, IReadOnlyList<string> FirewallSubnets)
{
    public static readonly BanLayersState Empty = new(0, [], 0, []);
}

/// <summary>
/// System layers of the ban: NRPT (one rule covering all subtrees at once, servers 127.0.0.1 —
/// nobody listens on localhost:53, so subtree names are never resolved) and firewall
/// (outbound Block by subnet). Managed via DnsClient/NetSecurity cmdlets, as in the
/// legacy scheme; writing requires administrator rights (elevated helper), reading does not.
/// </summary>
public interface IBanSystemOps
{
    Task<BanLayersState> GetLayersStateAsync(CancellationToken ct);

    Task ApplyNrptAsync(CancellationToken ct);

    Task RemoveNrptAsync(CancellationToken ct);

    Task ApplyFirewallAsync(CancellationToken ct);

    Task RemoveFirewallAsync(CancellationToken ct);

    Task FlushDnsCacheAsync(CancellationToken ct);
}

public sealed class PowerShellBanSystemOps(GuardConfig config, IPowerShellExecutor powershell) : IBanSystemOps
{
    public async Task<BanLayersState> GetLayersStateAsync(CancellationToken ct)
    {
        var name = PsLiteral(config.NrptRuleName);
        var group = PsLiteral(config.FirewallGroup);
        var (code, output) = await powershell.RunScriptAsync($$"""
            $nrpt = @(Get-DnsClientNrptRule | Where-Object { $_.DisplayName -eq {{name}} })
            $fw = @(Get-NetFirewallRule -Group {{group}} -ErrorAction SilentlyContinue)
            $ns = @($nrpt | ForEach-Object { $_.Namespace })
            $subnets = @($fw | Get-NetFirewallAddressFilter | ForEach-Object { $_.RemoteAddress })
            ConvertTo-Json -Compress -InputObject @{ nrptCount = $nrpt.Count; ns = $ns; fwCount = $fw.Count; subnets = $subnets }
            """, ct);
        ThrowIfFailed("NRPT/firewall status", code, output);
        return ParseLayersState(output);
    }

    public Task ApplyNrptAsync(CancellationToken ct) => RunAsync("NRPT apply", BuildNrptScript(apply: true), ct);

    public Task RemoveNrptAsync(CancellationToken ct) => RunAsync("NRPT remove", BuildNrptScript(apply: false), ct);

    public Task ApplyFirewallAsync(CancellationToken ct) => RunAsync("firewall apply", BuildFirewallScript(apply: true), ct);

    public Task RemoveFirewallAsync(CancellationToken ct) => RunAsync("firewall remove", BuildFirewallScript(apply: false), ct);

    public Task FlushDnsCacheAsync(CancellationToken ct) =>
        RunAsync("DNS cache flush", "Clear-DnsClientCache", ct);

    private async Task RunAsync(string what, string script, CancellationToken ct)
    {
        var (code, output) = await powershell.RunScriptAsync(script, ct);
        ThrowIfFailed(what, code, output);
    }

    /// <summary>One NRPT rule for all subtrees: apex + ".apex"; the rule is removed first — applying is idempotent.</summary>
    internal string BuildNrptScript(bool apply)
    {
        var name = PsLiteral(config.NrptRuleName);
        var remove = $"Get-DnsClientNrptRule | Where-Object {{ $_.DisplayName -eq {name} }} | Remove-DnsClientNrptRule -Force";
        if (!apply)
        {
            return remove;
        }

        var namespaces = string.Join(",", config.BanTrees.SelectMany(t => new[] { t, "." + t }).Select(PsLiteral));
        var comment = PsLiteral(config.NrptComment);
        return $"""
            {remove}
            Add-DnsClientNrptRule -Namespace @({namespaces}) -NameServers @('127.0.0.1') -DisplayName {name} -Comment {comment} | Out-Null
            """;
    }

    /// <summary>Outbound Block rules grouped by address family (IPv4/IPv6); the group is removed first — applying is idempotent.</summary>
    internal string BuildFirewallScript(bool apply)
    {
        var group = PsLiteral(config.FirewallGroup);
        var remove = $"Get-NetFirewallRule -Group {group} -ErrorAction SilentlyContinue | Remove-NetFirewallRule";
        if (!apply)
        {
            return remove;
        }

        var name = config.NrptRuleName;
        var rules = config.FirewallSubnets
            .GroupBy(subnet => subnet.Contains(':') ? "IPv6" : "IPv4")
            .Select(family => $"""
                New-NetFirewallRule -DisplayName {PsLiteral($"{name}: {family.Key}")} -Group {group} -Direction Outbound -Action Block -Profile Any -RemoteAddress @({string.Join(",", family.Select(PsLiteral))}) | Out-Null
                """);
        return $"{remove}\n{string.Join("\n", rules)}";
    }

    internal static BanLayersState ParseLayersState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new BanLayersState(
                root.GetProperty("nrptCount").GetInt32(),
                ReadStringArray(root, "ns"),
                root.GetProperty("fwCount").GetInt32(),
                ReadStringArray(root, "subnets"));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"unexpected NRPT/firewall status output: {json}", ex);
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(item => item.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [element.GetString() ?? ""];
    }

    private static void ThrowIfFailed(string what, int code, string output)
    {
        if (code != 0)
        {
            var tail = output.Length > 400 ? output[^400..] : output;
            throw new IOException($"{what} failed (exit {code}): {tail}");
        }
    }

    /// <summary>PowerShell single quotes with inner quotes doubled.</summary>
    internal static string PsLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
