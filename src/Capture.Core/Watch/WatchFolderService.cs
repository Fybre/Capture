using System.Diagnostics;
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

            var active = new ActiveWatch
            {
                Entry = entry,
                Folder = folder,
                Watcher = CreateWatcher(folder),
                Settler = settler
            };

            WireWatcher(active);
            active.Timer = new Timer(_ => Flush(active), null, 400, 400);
            _active.Add(active);
        }
    }

    private static FileSystemWatcher CreateWatcher(string folder) => new(folder)
    {
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        IncludeSubdirectories = false
        // EnableRaisingEvents is set by WireWatcher, only once handlers (including Error, for
        // reconnect-on-overflow) are attached — otherwise events raised in that gap are missed.
    };

    private void WireWatcher(ActiveWatch active)
    {
        void Note(string path)
        {
            var resolved = WatchPaths.Resolve(path, active.Folder);
            if (resolved is null)
                return;
            lock (active.Gate)
                active.Settler.Note(resolved);
        }

        active.Watcher.Created += (_, e) => Note(string.IsNullOrEmpty(e.Name) ? e.FullPath : e.Name);
        active.Watcher.Changed += (_, e) => Note(string.IsNullOrEmpty(e.Name) ? e.FullPath : e.Name);
        active.Watcher.Renamed += (_, e) => Note(string.IsNullOrEmpty(e.Name) ? e.FullPath : e.Name);
        active.Watcher.Error += (_, e) => Reconnect(active, e.GetException());
        active.Watcher.EnableRaisingEvents = true;
    }

    // FileSystemWatcher stops reliably raising events after an internal error — most commonly its
    // notification buffer overflowing under a burst of changes — and never recovers on its own.
    // Recreate it rather than silently losing all future file-arrival notifications for this folder.
    // Anything that happened during the gap is recovered by re-seeding the settler from the folder's
    // current contents (Note() is a no-op for anything already claimed, so this is safe to repeat).
    private void Reconnect(ActiveWatch active, Exception? error)
    {
        lock (active.Gate)
        {
            if (!_active.Contains(active))
                return; // this entry was stopped/replaced since the error was raised

            Trace.TraceError($"Watch folder '{active.Folder}' watcher error, reconnecting: {error}");
            active.Watcher.EnableRaisingEvents = false;
            active.Watcher.Dispose();

            try
            {
                active.Watcher = CreateWatcher(active.Folder);
            }
            catch (Exception ex)
            {
                // Folder likely gone/inaccessible — nothing more to do until the next Apply().
                Trace.TraceError($"Could not reconnect watcher for '{active.Folder}': {ex}");
                return;
            }

            WireWatcher(active);

            if (Directory.Exists(active.Folder))
            {
                foreach (var file in Directory.EnumerateFiles(active.Folder).Where(file => WatchPaths.IsWatchable(file, active.Folder)))
                    active.Settler.Note(file);
            }
        }
    }

    public bool ReportFailure(WatchFolderEntry entry, string path)
    {
        var active = _active.FirstOrDefault(item => item.Entry.Id == entry.Id);
        if (active is null)
            return false;

        lock (active.Gate)
            return active.Settler.ReleaseFailed(path);
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
        {
            Trace.TraceInformation($"Watch folder '{active.Folder}': {ready.Count} file(s) ready ({string.Join(", ", ready.Select(Path.GetFileName))})");
            FilesReady?.Invoke(active.Entry, ready);
        }
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
