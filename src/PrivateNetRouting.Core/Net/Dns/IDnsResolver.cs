using System.Net;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Resolution result: address, source (for logs) and response TTL (sec). Alternates are the other
/// addresses of the same answer (a few at most): a dial that fails on the first one tries them.
/// </summary>
public sealed record ResolveResult(IPAddress Ip, string Source, uint? TtlSeconds, IReadOnlyList<IPAddress>? Alternates = null);

public interface IDnsResolver
{
    Task<ResolveResult> ResolveAsync(string host, CancellationToken ct);
}
