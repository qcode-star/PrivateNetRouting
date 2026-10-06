using System.Net;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>Resolution result: address, source (for logs) and response TTL (sec).</summary>
public sealed record ResolveResult(IPAddress Ip, string Source, uint? TtlSeconds);

public interface IDnsResolver
{
    Task<ResolveResult> ResolveAsync(string host, CancellationToken ct);
}
