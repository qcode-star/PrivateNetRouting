using System.Net;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>Результат резолва: адрес, источник (для логов) и TTL ответа (сек).</summary>
public sealed record ResolveResult(IPAddress Ip, string Source, uint? TtlSeconds);

public interface IDnsResolver
{
    Task<ResolveResult> ResolveAsync(string host, CancellationToken ct);
}
