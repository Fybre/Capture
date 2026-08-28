namespace Capture.Core.Watch;

public interface IWatchFolderService : IDisposable
{
    bool IsRunning { get; }

    IReadOnlyList<WatchFolderEntry> ActiveFolders { get; }

    event Action<WatchFolderEntry, IReadOnlyList<string>>? FilesReady;

    void Apply(IReadOnlyList<WatchFolderEntry> entries);
}
