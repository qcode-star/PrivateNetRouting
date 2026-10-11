using System.Net;
using PrivateNetRouting.Core.AppUpdate;
using PrivateNetRouting.Core.Launcher;
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

/// <summary>Release feed with a canned answer: a fixed release, a fixed error, or "nothing".</summary>
internal sealed class FakeAppReleaseFeed((string Tag, string Url)? release, Exception? error = null) : IAppReleaseFeed
{
    public int Calls { get; private set; }

    public Task<(string Tag, string Url)?> GetLatestReleaseAsync(CancellationToken ct)
    {
        Calls++;
        return error is not null
            ? Task.FromException<(string Tag, string Url)?>(error)
            : Task.FromResult(release);
    }
}

/// <summary>
/// Closer of a foreign application: alive PIDs are scripted, both close paths are recorded.
/// A graceful close succeeds only for PIDs that are not in <see cref="NoWindow"/> and removes the
/// process (the window close takes the Electron tree down); KillTree removes it unless it is
/// listed in <see cref="SurvivesKill"/> (a zombie the caller must report).
/// </summary>
internal sealed class FakeAppCloser : IRunningAppCloser
{
    public HashSet<int> Alive { get; } = [];

    /// <summary>PIDs whose window close reports "no window" (they go straight to the forced kill).</summary>
    public HashSet<int> NoWindow { get; } = [];

    /// <summary>PIDs that outlive the forced kill.</summary>
    public HashSet<int> SurvivesKill { get; } = [];

    public List<int> GracefulCalls { get; } = [];

    public List<int> KillCalls { get; } = [];

    public bool CloseGracefully(int processId)
    {
        GracefulCalls.Add(processId);
        if (NoWindow.Contains(processId))
        {
            return false;
        }

        Alive.Remove(processId);
        return true;
    }

    public bool Exists(int processId) => Alive.Contains(processId);

    public void KillTree(int processId)
    {
        KillCalls.Add(processId);
        if (!SurvivesKill.Contains(processId))
        {
            Alive.Remove(processId);
        }
    }
}
