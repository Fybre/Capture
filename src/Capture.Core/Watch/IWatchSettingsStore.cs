namespace Capture.Core.Watch;

public interface IWatchSettingsStore
{
    Task<WatchSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(WatchSettings settings, CancellationToken cancellationToken = default);
}
