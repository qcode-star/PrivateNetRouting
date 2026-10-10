using Windows.ApplicationModel.Store.Preview.InstallControl;

namespace PrivateNetRouting.Core.Updater;

/// <summary>Lifecycle phase of a Store update, mapped from AppInstallState (WinRT).</summary>
public enum StoreInstallPhase
{
    /// <summary>Queued or paused: the Store has not started moving bytes yet.</summary>
    Waiting,

    /// <summary>The Store is downloading the package (its own CDN, not the proxy chain).</summary>
    Downloading,

    /// <summary>The package is on disk and the Store is registering it.</summary>
    Installing,

    Completed,

    Canceled,

    Error,
}

public sealed record StoreInstallStatus(
    StoreInstallPhase Phase,
    ulong TotalBytes,
    ulong DoneBytes,
    double Percent,
    string? Error);

/// <summary>An update queued in the Microsoft Store: status is polled, Cancel asks the Store to stop.</summary>
public interface IStoreInstallHandle
{
    StoreInstallStatus GetStatus();

    void Cancel();
}

/// <summary>
/// The Microsoft Store update machinery for one app: the update search by the Store product id
/// and the update start by the package family name. The Store downloads and registers the
/// package itself — no proxy chain and no admin rights involved.
/// </summary>
public interface IStoreUpdateSource
{
    /// <summary>Whether the Store catalog offers an update newer than the installed version.</summary>
    Task<bool> HasUpdateAsync(string storeProductId, CancellationToken ct);

    /// <summary>Queues the update for the package family (idempotent: an already queued one is returned).</summary>
    Task<IStoreInstallHandle> StartUpdateAsync(string packageFamilyName, CancellationToken ct);
}

/// <summary>The real Store source (WinRT AppInstallManager; needs no package identity).</summary>
public sealed class StoreUpdateSource : IStoreUpdateSource
{
    public async Task<bool> HasUpdateAsync(string storeProductId, CancellationToken ct)
    {
        var manager = new AppInstallManager();
        var item = await manager.SearchForUpdatesAsync(storeProductId, "").AsTask(ct).ConfigureAwait(false);
        return item is not null;
    }

    public async Task<IStoreInstallHandle> StartUpdateAsync(string packageFamilyName, CancellationToken ct)
    {
        var manager = new AppInstallManager();
        var item = await manager.UpdateAppByPackageFamilyNameAsync(packageFamilyName).AsTask(ct).ConfigureAwait(false);
        return new StoreInstallHandle(item);
    }

    private sealed class StoreInstallHandle(AppInstallItem item) : IStoreInstallHandle
    {
        public StoreInstallStatus GetStatus()
        {
            var status = item.GetCurrentStatus();
            var phase = status.InstallState switch
            {
                AppInstallState.Completed => StoreInstallPhase.Completed,
                AppInstallState.Canceled => StoreInstallPhase.Canceled,
                AppInstallState.Error => StoreInstallPhase.Error,
                AppInstallState.Installing or AppInstallState.RestoringData => StoreInstallPhase.Installing,
                AppInstallState.Downloading => StoreInstallPhase.Downloading,
                _ => StoreInstallPhase.Waiting,
            };
            return new StoreInstallStatus(
                phase,
                status.DownloadSizeInBytes,
                status.BytesDownloaded,
                status.PercentComplete,
                status.ErrorCode?.Message);
        }

        public void Cancel() => item.Cancel();
    }
}
