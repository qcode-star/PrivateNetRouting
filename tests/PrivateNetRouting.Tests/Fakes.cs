using System.Net;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.SystemOps;

namespace PrivateNetRouting.Tests;

/// <summary>Manual clock for time tests (TTL, cooldowns, marker throttling).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan span) => _utcNow += span;
}

/// <summary>Process lookup by name: canned answers, filtered by the command-line substring like WMI does.</summary>
internal sealed class FakeFinderByName : IProcessFinder
{
    public Dictionary<string, List<FoundProcess>> ByName { get; } = [];

    public IReadOnlyList<FoundProcess> Find(string processName, string commandLineContains) =>
        ByName.TryGetValue(processName, out var found)
            ? found.Where(p => p.CommandLine.Contains(commandLineContains, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

    public IReadOnlyList<FoundProcess> FindByNameContains(string namePart) => [];

    public FoundProcess? FindById(int processId) => null;
}

/// <summary>Resolver with a canned answer; counts calls.</summary>
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
