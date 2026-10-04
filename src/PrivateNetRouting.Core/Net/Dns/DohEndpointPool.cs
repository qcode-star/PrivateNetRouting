using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Пул DoH-резолверов. Стартовый набор — из конфига; при наличии источника обнаружения
/// (конфиг tailnet) список пополняется и перепроверяется по таймеру. Пробы гонятся параллельно,
/// живые эндпоинты ставятся вперёд (мёртвый узел вверху админского списка не тормозит резолв);
/// отказавший эндпоинт уходит в кулдаун. Каждое обновление пула пишется в Debug, изменение
/// живого набора — одной строкой в Info, повторы молчат. Если обнаружение ничего не вернуло —
/// остаётся стартовый набор.
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
    private readonly PeriodicTimer _timer;
    private List<DohEndpoint> _pool;
    private List<DohEndpoint>? _lastLive;
    private int _refreshRunning;

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
        _timer = new PeriodicTimer(_refreshInterval);
        _ = Task.Run(RefreshForeverAsync);
    }

    /// <summary>Кандидаты для резолва: без остывающих; если остывают все — все.</summary>
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

    private async Task RefreshForeverAsync()
    {
        await SafeRefreshAsync();
        while (await _timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await SafeRefreshAsync();
        }
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"doh pool: refresh failed: {ex.Message}");
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _refreshRunning, 1) == 1)
        {
            return;
        }

        try
        {
            List<DohEndpoint> discovered = [];
            if (_source is not null)
            {
                discovered = await TryDiscoverAsync(ct);
            }
            List<DohEndpoint> merged;
            if (discovered.Count > 0)
            {
                merged = [.. discovered, .. _seed.Where(s => !discovered.Contains(s))];
            }
            else
            {
                merged = [.. _seed];
            }
            if (merged.Count == 0)
            {
                return;
            }

            var probed = await Task.WhenAll(merged.Select(async endpoint =>
                (endpoint, alive: await ProbeAsync(endpoint, ct))));

            var live = probed.Where(p => p.alive).Select(p => p.endpoint).ToList();
            var rest = probed.Where(p => !p.alive).Select(p => p.endpoint).ToList();

            lock (_gate)
            {
                _pool = [.. live, .. rest];
                _deadUntil.Clear();
            }

            _log.Debug($"doh pool: refresh: live=[{string.Join(", ", live)}] rest=[{string.Join(", ", rest)}]");
            if (!live.SequenceEqual(_lastLive ?? []))
            {
                _lastLive = live;
                _log.Info($"doh pool: live set changed: live=[{string.Join(", ", live)}] rest=[{string.Join(", ", rest)}]");
            }
        }
        finally
        {
            Volatile.Write(ref _refreshRunning, 0);
        }
    }

    private async Task<List<DohEndpoint>> TryDiscoverAsync(CancellationToken ct)
    {
        try
        {
            return [.. await _source!.GetEndpointsAsync(ct)];
        }
        catch (Exception ex)
        {
            _log.Warn($"doh pool: discovery failed, keeping the seed set: {ex.Message}");
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

    public void Dispose() => _timer.Dispose();
}
