using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Pool of DoH resolvers. The initial set comes from the config; when a discovery source
/// (tailnet config) is present, the list is replenished and rechecked on a timer that starts with
/// the first refresh request (the pool does no work until it is asked for an address). Probes run
/// in parallel, live endpoints are moved to the front (a dead node at the top of the admin
/// list does not slow resolution down); a failing endpoint goes into cooldown. Every pool
/// refresh is written to Debug, a change of the live set — as a single line in Info, repeats
/// stay silent. If discovery returned nothing (the sidecar is down, the netmap call failed) the
/// current set remains — endpoints found earlier are not dropped. A pool that has no live
/// endpoint — built before the chain came up, or the chain was down at the last refresh — is
/// refreshed on demand (<see cref="EnsureLiveAsync"/>, single-flight and throttled) and retried
/// on a short timer instead of waiting out the long refresh interval.
/// </summary>
public sealed partial class DohEndpointPool : IDisposable
{
    private readonly Socks5Client _socks;
    private readonly IReadOnlyList<DohEndpoint> _seed;
    private readonly IDohEndpointSource? _source;
    private readonly Func<DohEndpoint, CancellationToken, Task<bool>> _probe;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _deadCooldown;
    private readonly string _probeName;
    private readonly FileLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<DohEndpoint, DateTimeOffset> _deadUntil = [];
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private List<DohEndpoint> _pool;
    private List<DohEndpoint>? _lastLive;

    /// <summary>Message of the last discovery failure (refreshes are single-flight): a repeat stays out of the Warn level.</summary>
    private string? _lastDiscoveryFailure;
    private Task? _inflight;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    /// <summary>While no endpoint is live the background refresh retries this often (capped by the refresh interval).</summary>
    private static readonly TimeSpan EmptyRetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>The minimum gap between two on-demand refreshes of a pool that stays empty.</summary>
    private static readonly TimeSpan OnDemandMinGap = TimeSpan.FromSeconds(5);

    public DohEndpointPool(
        Socks5Client socks,
        IReadOnlyList<DohEndpoint> seed,
        FileLog log,
        TimeSpan refreshInterval,
        TimeSpan probeTimeout,
        TimeSpan deadCooldown,
        string probeName,
        IDohEndpointSource? source = null,
        TimeProvider? time = null,
        Func<DohEndpoint, CancellationToken, Task<bool>>? prober = null)
    {
        _socks = socks;
        _seed = seed;
        _source = source;
        _probe = prober ?? DefaultProbeAsync;
        _log = log;
        _refreshInterval = refreshInterval;
        _probeTimeout = probeTimeout;
        _deadCooldown = deadCooldown;
        _probeName = probeName;
        _time = time ?? TimeProvider.System;
        _pool = [.. seed];
        _stopToken = _stop.Token;
        // no background work here: a pool that is never asked for an address (every `pnr` command
        // composes a runtime, most never resolve a name) must not spawn `tailscale debug netmap`
        // and probe the endpoints — the refresh loop starts with the first refresh
    }

    private int _loopStarted;

    /// <summary>Starts the periodic refresh once, on the first refresh request.</summary>
    private void EnsureLoopStarted()
    {
        if (_stopToken.IsCancellationRequested || Interlocked.Exchange(ref _loopStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(RefreshForeverAsync);
    }

    /// <summary>Resolution candidates: excluding cooling-down ones; if all are cooling down — all of them.</summary>
    public IReadOnlyList<DohEndpoint> SnapshotAlive()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var alive = _pool.Where(e => !_deadUntil.TryGetValue(e, out var until) || until <= now).ToList();
            return alive.Count > 0 ? alive : _pool;
        }
    }

    public void MarkDead(DohEndpoint endpoint)
    {
        lock (_gate)
        {
            _deadUntil[endpoint] = _time.GetUtcNow() + _deadCooldown;
        }
    }

    public void MarkAlive(DohEndpoint endpoint)
    {
        lock (_gate)
        {
            _deadUntil.Remove(endpoint);
        }
    }

    /// <summary>Whether the last refresh found at least one endpoint answering the probe.</summary>
    public bool HasLive
    {
        get
        {
            lock (_gate)
            {
                return _lastLive is { Count: > 0 };
            }
        }
    }

    /// <summary>
    /// Refreshes the pool when it has no live endpoint (nothing probed yet, or the last refresh
    /// found none) — the resolver calls this before a lookup, so a pool built before the chain
    /// came up fills itself as soon as the first name is asked for. Concurrent callers share one
    /// refresh; a pool that stays empty is retried at most once per <see cref="OnDemandMinGap"/>.
    /// </summary>
    public async Task EnsureLiveAsync(CancellationToken ct)
    {
        EnsureLoopStarted();
        lock (_gate)
        {
            if (_lastLive is { Count: > 0 })
            {
                return;
            }

            if (_inflight is null && _time.GetUtcNow() - _lastAttempt < OnDemandMinGap)
            {
                return;
            }
        }

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshForeverAsync()
    {
        try
        {
            // waiting first: the refresh that started this loop is already running or done
            while (!_stopToken.IsCancellationRequested)
            {
                var delay = HasLive
                    ? _refreshInterval
                    : TimeSpan.FromTicks(Math.Min(_refreshInterval.Ticks, EmptyRetryInterval.Ticks));
                await Task.Delay(delay, _stopToken).ConfigureAwait(false);
                await SafeRefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the pool was disposed
        }
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"doh pool: refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One refresh of the pool; single-flight: a caller that arrives while a refresh is running
    /// waits for that one instead of starting another (or returning before it is done). The
    /// token only abandons the wait — the refresh itself runs until it finishes or the pool is
    /// disposed.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        EnsureLoopStarted();
        Task run;
        lock (_gate)
        {
            _inflight ??= Task.Run(RunRefreshAsync);
            run = _inflight;
        }

        await run.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunRefreshAsync()
    {
        try
        {
            var ct = _stopToken;
            List<DohEndpoint> discovered = [];
            if (_source is not null)
            {
                discovered = await TryDiscoverAsync(ct).ConfigureAwait(false);
            }

            List<DohEndpoint> merged;
            if (discovered.Count > 0)
            {
                merged = [.. discovered, .. _seed.Where(s => !discovered.Contains(s))];
            }
            else
            {
                // nothing discovered (the sidecar is down or the netmap is empty): keep what the
                // pool already knows next to the seed — a refresh over a down chain must not
                // throw the endpoints found earlier away
                List<DohEndpoint> current;
                lock (_gate)
                {
                    current = [.. _pool];
                }

                merged = [.. _seed, .. current.Where(c => !_seed.Contains(c))];
            }

            if (merged.Count == 0)
            {
                return;
            }

            var probed = await Task.WhenAll(merged.Select(async endpoint =>
                (endpoint, alive: await ProbeAsync(endpoint, ct).ConfigureAwait(false)))).ConfigureAwait(false);

            var live = probed.Where(p => p.alive).Select(p => p.endpoint).ToList();
            var rest = probed.Where(p => !p.alive).Select(p => p.endpoint).ToList();

            bool changed;
            lock (_gate)
            {
                _pool = [.. live, .. rest];
                _deadUntil.Clear();
                changed = !live.SequenceEqual(_lastLive ?? []);
                _lastLive = live;
            }

            _log.Debug($"doh pool: refresh: live=[{string.Join(", ", live)}] rest=[{string.Join(", ", rest)}]");
            if (changed)
            {
                _log.Info($"doh pool: live set changed: live=[{string.Join(", ", live)}] rest=[{string.Join(", ", rest)}]");
            }
        }
        finally
        {
            lock (_gate)
            {
                _lastAttempt = _time.GetUtcNow();
                _inflight = null;
            }
        }
    }

    private async Task<List<DohEndpoint>> TryDiscoverAsync(CancellationToken ct)
    {
        try
        {
            var found = new List<DohEndpoint>(await _source!.GetEndpointsAsync(ct));
            _lastDiscoveryFailure = null;
            return found;
        }
        catch (Exception ex)
        {
            // the refresh is periodic: a failure that repeats verbatim is Debug, only the first
            // one (or a changed one) is a Warn
            var failure = ex.Message;
            if (failure == _lastDiscoveryFailure)
            {
                _log.Debug($"doh pool: discovery failed again, keeping the seed set: {failure}");
            }
            else
            {
                _log.Warn($"doh pool: discovery failed, keeping the seed set: {failure}");
                _lastDiscoveryFailure = failure;
            }

            return [];
        }
    }

    private async Task<bool> ProbeAsync(DohEndpoint endpoint, CancellationToken ct) =>
        await _probe(endpoint, ct);

    private async Task<bool> DefaultProbeAsync(DohEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            await DohClient.QueryAsync(_socks, endpoint, _probeName, _probeTimeout, ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stops the background refresh (the token source is left to the GC: a refresh in flight still reads its token).</summary>
    public void Dispose() => _stop.Cancel();
}
