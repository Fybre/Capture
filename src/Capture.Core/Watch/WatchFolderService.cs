using Capture.Core.Import;

namespace Capture.Core.Watch;

public sealed class WatchFolderService : IWatchFolderService
{
    private sealed class ActiveWatch
    {
        public readonly object Gate = new();
        public required WatchFolderEntry Entry;
        public required string Folder;
        public required FileSystemWatcher Watcher;
        public required WatchSettler Settler;
        public Timer? Timer;
    }

    private readonly List<ActiveWatch> _active = [];

    public bool IsRunning => _active.Count > 0;

    public IReadOnlyList<WatchFolderEntry> ActiveFolders => _active.Select(item => item.Entry).ToList();

    public event Action<WatchFolderEntry, IReadOnlyList<string>>? FilesReady;

    public void Apply(IReadOnlyList<WatchFolderEntry> entries)
    {
        Stop();

        foreach (var entry in entries)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Folder) || !Directory.Exists(entry.Folder))
                continue;

            var folder = Path.GetFullPath(entry.Folder);
            var settler = new WatchSettler(TimeSpan.FromMilliseconds(Math.Max(250, entry.SettleMilliseconds)));

            // Seed the settler with the folder's current contents before the watcher/timer
            // start so the periodic Flush() timer can never observe a partially-seeded settler.
            foreach (var file in Directory.EnumerateFiles(folder).Where(file => WatchPaths.IsWatchable(file, folder)))
                settler.Note(file);

            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            var active = new ActiveWatch
            {
                Entry = entry,
                Folder = folder,
                Watcher = watcher,
                Settler = settler
            };

            void Note(string path)
            {
                var resolved = WatchPaths.Resolve(path, folder);
                if (resolved is null)
                    return;
                lock (active.Gate)
                    settler.Note(resolved);
            }

            watcher.Created += (_, e) => Note(string.IsNullOrEmpty(e.Name) ? e.FullPath : e.Name);
            watcher.Changed += (_, e) => Note(string.IsNullOrEmpty(e.Name) ? e.FullPath : e.Name);
            watcher.Renamed += (_, e) => Note(string.IsNullOrEmpty(e.Name) ? e.FullPath : e.Name);

            active.Timer = new Timer(_ => Flush(active), null, 400, 400);
            _active.Add(active);
        }
    }

    public void Dispose() => Stop();

    private void Flush(ActiveWatch active)
    {
        IReadOnlyList<string> ready;
        lock (active.Gate)
        {
            active.Settler.ReleaseGone(File.Exists);
            ready = active.Settler.TakeReady(DateTimeOffset.Now, IsUnlocked);
        }

        if (ready.Count > 0)
            FilesReady?.Invoke(active.Entry, ready);
    }

    private static bool IsUnlocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void Stop()
    {
        foreach (var active in _active)
        {
            active.Timer?.Dispose();
            active.Watcher.EnableRaisingEvents = false;
            active.Watcher.Dispose();
        }

        _active.Clear();
    }
}
