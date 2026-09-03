using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Diagnostics;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.Core.Store;
using Capture.Core.Watch;
using Capture.Export;
using Capture.Scanner;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class MainViewModel
{
    private readonly Queue<(string Path, WatchFolderEntry Entry)> _watchQueue = new();
    private readonly HashSet<string> _watchQueued = new(StringComparer.OrdinalIgnoreCase);
    private WatchSettings _watchSettings = new();
    private bool _watchProcessing;
    private CaptureBatch? _lastManualBatch;

    [ObservableProperty]
    private string _watchStatus = "Watch off";

    private async Task CheckForUpdatesAsync()
    {
        var result = await _updateCheck.CheckForUpdateAsync().ConfigureAwait(true);
        if (!result.IsUpdateAvailable)
            return;

        var releaseUrl = result.ReleaseUrl;
        _toasts.ShowInfo(
            $"Capture {result.LatestVersion} is available — click to view the release.",
            releaseUrl is null ? null : () => Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }));
    }

    private void OnWatchFilesReady(WatchFolderEntry entry, IReadOnlyList<string> files)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var file in files)
            {
                var resolved = string.IsNullOrWhiteSpace(entry.Folder) ? null : WatchPaths.Resolve(file, entry.Folder);
                if (resolved is null)
                    continue;
                if (_watchQueued.Add(resolved))
                    _watchQueue.Enqueue((resolved, entry));
            }

            _ = ProcessWatchQueueAsync();
        });
    }

    private async Task ProcessWatchQueueAsync()
    {
        if (_watchProcessing || IsBusy || _watchQueue.Count == 0)
            return;

        _watchProcessing = true;
        try
        {
            while (_watchQueue.Count > 0)
            {
                var pending = new List<(string Path, WatchFolderEntry Entry)>();
                while (_watchQueue.Count > 0)
                    pending.Add(_watchQueue.Dequeue());

                // Files queued from different watch folders carry different profiles/roots —
                // import each folder's files as its own batch rather than mixing them together.
                foreach (var group in pending.GroupBy(item => item.Entry.Id))
                {
                    var entry = group.First().Entry;
                    var batch = new List<string>();
                    foreach (var (path, _) in group)
                    {
                        if (File.Exists(path))
                            batch.Add(path);
                        else
                            _watchQueued.Remove(path);
                    }

                    if (batch.Count == 0)
                        continue;

                    var profile = entry.ProfileId is { } id
                        ? Profiles.FirstOrDefault(item => item.Id == id)
                        : null;
                    await ImportPathsAsync(batch, DocumentSource.Watch, profile, entry.Folder, entry);
                    foreach (var path in batch)
                        _watchQueued.Remove(path);
                }
            }
        }
        finally
        {
            _watchProcessing = false;
            if (_watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
    }

    private async Task ApplyWatchAsync()
    {
        _watchSettings = await _watchStore.LoadAsync().ConfigureAwait(true);
        _watch.Apply(_watchSettings.WatchFolders);
        var active = _watch.ActiveFolders;
        WatchStatus = active.Count switch
        {
            0 => "Watch off",
            1 => $"Watching {active[0].Folder}",
            _ => $"Watching {active.Count} folders"
        };
        ApplyTheme(_watchSettings.Theme);
        _debugLog.SetEnabled(_watchSettings.DebugMode);
        await RunAutoCleanupIfEnabledAsync().ConfigureAwait(true);
    }

    /// <summary>Runs at each app startup and whenever Settings is saved (both via ApplyWatchAsync) — see
    /// WatchSettings.AutoDeleteExportedDocuments and WatchSettings.TrashRetentionDays. Distinct from
    /// Settings' "Clean up now" button (SettingsViewModel.CleanUpOldDocumentsAsync), which is an
    /// immediate, unconditional, user-initiated sweep rather than these age-gated automatic ones. The
    /// exported-document sweep now soft-deletes (reversible, via Trash) rather than purging outright;
    /// the trash sweep below is what actually removes anything for good, once it's past retention.</summary>
    private async Task RunAutoCleanupIfEnabledAsync()
    {
        var reloadNeeded = false;

        if (_watchSettings.AutoDeleteExportedDocuments)
        {
            var stale = DocumentCleanup.SelectStale(
                await _store.GetAllAsync().ConfigureAwait(true),
                _watchSettings.AutoDeleteExportedDocumentsAfterDays,
                DateTimeOffset.Now);
            if (stale.Count > 0)
            {
                foreach (var document in stale)
                    await _store.SoftDeleteAsync(document.Id).ConfigureAwait(true);

                Trace.TraceInformation(
                    $"Auto-cleanup trashed {stale.Count} exported document(s) older than {_watchSettings.AutoDeleteExportedDocumentsAfterDays} day(s)");
                reloadNeeded = true;
            }
        }

        var expiredTrash = DocumentCleanup.SelectExpiredTrash(
            await _store.GetTrashedAsync().ConfigureAwait(true),
            _watchSettings.TrashRetentionDays,
            DateTimeOffset.Now);
        if (expiredTrash.Count > 0)
        {
            foreach (var document in expiredTrash)
                await _store.PurgeAsync(document.Id).ConfigureAwait(true);

            Trace.TraceInformation(
                $"Trash purged {expiredTrash.Count} document(s) past the {_watchSettings.TrashRetentionDays}-day retention period");
            reloadNeeded = true;
        }

        if (reloadNeeded)
            await ReloadDocumentsAsync().ConfigureAwait(true);
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is not { } app)
            return;

        app.RequestedThemeVariant = ThemeVariantMapper.Map(theme);
    }

    private void MoveWatchFile(string path, string? watchRoot, WatchFolderEntry? watchFolderEntry, bool success)
    {
        if (string.IsNullOrWhiteSpace(watchRoot) || !File.Exists(path))
            return;

        try
        {
            WatchFileMover.Move(path, watchRoot, success);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to file away watch file '{path}': {ex}");
            var fileName = Path.GetFileName(path);
            if (watchFolderEntry is null)
            {
                StatusText = $"Couldn't file away {fileName}: {ex.Message}";
                return;
            }

            StatusText = _watch.ReportFailure(watchFolderEntry, path)
                ? $"Couldn't file away {fileName} ({ex.Message}) — will retry"
                : $"Couldn't file away {fileName} after repeated attempts ({ex.Message}) — left in the watch folder, needs manual attention";
        }
    }
}
