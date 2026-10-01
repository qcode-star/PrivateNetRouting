using System.Net;
using PrivateNetRouting.Core.Net.Dns;

namespace PrivateNetRouting.Tests;

/// <summary>Ручные часы для тестов времени (TTL, кулдауны, троттлинг маркера).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan span) => _utcNow += span;
}

/// <summary>Резолвер с запрограммированным ответом; считает вызовы.</summary>
internal sealed class FakeResolver(Func<string, ResolveResult>? factory = null) : IDnsResolver
{
    private readonly Func<string, ResolveResult> _factory =
        factory ?? (_ => new ResolveResult(IPAddress.Loopback, "fake", 300));

    public int Calls { get; private set; }

    public Task<ResolveResult> ResolveAsync(string host, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_factory(host));
    }
}
