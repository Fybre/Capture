namespace Capture.Core.Watch;

public interface IWatchFolderService : IDisposable
{
    bool IsRunning { get; }

    IReadOnlyList<WatchFolderEntry> ActiveFolders { get; }

    event Action<WatchFolderEntry, IReadOnlyList<string>>? FilesReady;

    void Apply(IReadOnlyList<WatchFolderEntry> entries);

    /// <summary>
    /// Reports that processing a file <see cref="FilesReady"/> handed out for <paramref name="entry"/>
    /// failed, so it can be retried on a later flush instead of staying claimed (and therefore never
    /// offered again) forever. Returns true if the path will be retried, false if it's exhausted its
    /// retry budget (or the entry/path isn't tracked) and needs manual attention.
    /// </summary>
    bool ReportFailure(WatchFolderEntry entry, string path);
}
