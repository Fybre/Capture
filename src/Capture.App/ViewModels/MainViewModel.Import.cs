using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
using Capture.Core.CaptureProfiles;
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
    // Trash-aware: reloads whichever list is currently showing (see ShowTrash in
    // MainViewModel.Documents.cs), so every existing caller (startup, auto-cleanup, Settings save)
    // correctly refreshes the Trash view instead of silently switching back to the normal list if
    // that's what the reviewer happened to be looking at.
    private async Task ReloadDocumentsAsync()
    {
        var documents = ShowTrash
            ? await _store.GetTrashedAsync().ConfigureAwait(true)
            : await _store.GetAllAsync().ConfigureAwait(true);

        // The document store has a stable canonical order: active batches oldest-first and Trash
        // newest-deleted-first. Apply the user's one global display preference here so Preview and
        // the Table groups are built from exactly the same ordered source collection.
        if ((!ShowTrash && _watchSettings.InboxOrder == InboxOrder.NewestFirst) ||
            (ShowTrash && _watchSettings.InboxOrder == InboxOrder.OldestFirst))
        {
            documents = documents.Reverse().ToList();
        }

        // Each document's own index file is a genuinely separate read, but every document sharing a
        // batch was independently re-reading and re-deserializing that same batch index file — a
        // shared, dedup'd cache (keyed by batch id, populated at most once per batch this pass) turns an
        // O(documents) number of batch-file reads into O(distinct batches). The per-document reads
        // themselves are still N, but independent file I/O, so running them concurrently cuts the wall
        // time of a full reload roughly by the available core count instead of paying every read's
        // latency serially.
        var batchValuesCache = new System.Collections.Concurrent.ConcurrentDictionary<Guid, Task<IReadOnlyList<IndexValue>>>();
        var rows = new DocumentRow[documents.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, documents.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (index, ct) => rows[index] = await CreateRowAsync(documents[index], batchValuesCache).ConfigureAwait(false))
            .ConfigureAwait(true);

        Documents.Clear();
        SelectedDocuments.Clear();
        foreach (var row in rows)
            Documents.Add(row);
        RefreshBatchAccents();
        RefreshDocumentGroups();
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFilesAsync()
    {
        var files = await _dialogs.PickFilesAsync();
        if (files.Count == 0)
            return;
        await ImportPathsAsync(files);
    }

    /// <summary>Handles file(s)/folder(s) dropped onto the window from Finder/Explorer — the drop
    /// target itself lives in MainWindow's code-behind (OS-level drag-and-drop isn't something a
    /// ViewModel can subscribe to directly); this is where it hands off into the same import pipeline
    /// <see cref="ImportFilesAsync"/> uses, so a drop behaves identically to picking files via the
    /// toolbar button. Folders are expanded one level deep, matching <see cref="ImportFolderAsync"/>.</summary>
    public async Task ImportDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanImport())
            return;

        var files = paths
            .SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path)
                : [path])
            .Where(ImportFormats.IsSupported)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            StatusText = "No supported files in the dropped item(s)";
            StatusIsError = true;
            return;
        }

        await ImportPathsAsync(files);
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
            return;

        IsBusy = true;
        try
        {
            StatusText = $"Importing folder {folder}…";
            var files = Directory.EnumerateFiles(folder)
                .Where(ImportFormats.IsSupported)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                StatusText = "No supported files in that folder";
                StatusIsError = true;
                return;
            }

            await ImportPathsAsync(files);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportPathsAsync(
        IReadOnlyList<string> paths,
        DocumentSource source = DocumentSource.Import,
        string? watchRoot = null,
        WatchFolderEntry? watchFolderEntry = null,
        bool manageBusy = true)
    {
        if (manageBusy)
            IsBusy = true;
        try
        {
            var profileId = watchFolderEntry?.CaptureProfileId;
            var profile = profileId is { } id
                ? CaptureProfiles.FirstOrDefault(item => item.Id == id)
                : SelectedCaptureProfile ?? BuiltInCaptureProfiles.Unsorted;
            if (profile is null)
            {
                StatusText = "Choose a Capture Profile before importing";
                StatusIsError = true;
                return;
            }

            var contentHashes = new string[paths.Count];
            for (var index = 0; index < paths.Count; index++)
                contentHashes[index] = await ComputeContentHashAsync(paths[index]).ConfigureAwait(true);

            // One batched lookup for the whole import instead of one round trip per file — the set of
            // hashes that already exist in the store, checked below against each file's own hash.
            var existingHashes = _watchSettings.DuplicateImportBehavior == DuplicateImportBehavior.Skip
                ? (await _store.FindByContentHashesAsync(contentHashes).ConfigureAwait(true))
                    .Select(document => document.ContentHash)
                    .Where(hash => !string.IsNullOrEmpty(hash))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];

            var accepted = new List<string>();
            var acceptedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedDuplicates = 0;
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                var contentHash = contentHashes[index];
                var skipDuplicate = _watchSettings.DuplicateImportBehavior == DuplicateImportBehavior.Skip
                    && (!acceptedHashes.Add(contentHash) || existingHashes.Contains(contentHash));
                if (skipDuplicate)
                {
                    skippedDuplicates++;
                    MoveWatchFile(path, watchRoot, watchFolderEntry, success: true);
                    continue;
                }
                accepted.Add(path);
            }

            var autoExportStatus = string.Empty;
            if (accepted.Count > 0)
            {
                StatusText = $"Processing {accepted.Count} file(s) with {profile.Name}…";
                var channel = watchFolderEntry is null ? "manual" : $"watch:{watchFolderEntry.Id:N}";
                var automated = watchFolderEntry is not null;
                var result = await _captureWorkflow.ExecuteAsync(
                    profile, accepted, source, channel,
                    startNewBatch: automated,
                    closeBatchWhenFinished: automated).ConfigureAwait(true);
                if (!automated)
                    await RefreshManualBatchStateAsync().ConfigureAwait(true);
                foreach (var path in accepted) MoveWatchFile(path, watchRoot, watchFolderEntry, success: true);
                await ReloadDocumentsAsync().ConfigureAwait(true);
                autoExportStatus = profile.AutoExportReadyDocuments
                    ? await AutoExportImportedDocumentsAsync(result.Materialized.Documents).ConfigureAwait(true)
                    : string.Empty;
                SelectedDocument = result.Materialized.Documents.Count == 0
                    ? null
                    : Documents.FirstOrDefault(row => row.Id == result.Materialized.Documents[^1].Id);
            }

            var suffix = skippedDuplicates > 0 ? $" — {skippedDuplicates} skipped as duplicate" : string.Empty;
            var importedStatus = accepted.Count == 0
                ? $"All {paths.Count} file(s) skipped as duplicates"
                : $"Imported {accepted.Count} file(s){suffix}";
            StatusText = string.IsNullOrEmpty(autoExportStatus)
                ? importedStatus
                : $"{importedStatus} — {autoExportStatus}";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
            foreach (var path in paths) MoveWatchFile(path, watchRoot, watchFolderEntry, success: false);
        }
        finally
        {
            if (manageBusy)
                IsBusy = false;
            if (manageBusy && !_watchProcessing && _watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
    }

    private async Task<string> AutoExportImportedDocumentsAsync(IReadOnlyList<CaptureDocument> imported)
    {
        var exported = 0;
        var failed = 0;
        var left = 0;
        foreach (var document in imported)
        {
            var row = Documents.FirstOrDefault(item => item.Id == document.Id);
            if (row is null) continue;
            switch (await ExportDocumentAsync(row).ConfigureAwait(true))
            {
                case ExportOutcome.Exported:
                case ExportOutcome.ExportedAndRemoved:
                    exported++;
                    break;
                case ExportOutcome.Failed:
                    failed++;
                    break;
                default:
                    left++;
                    break;
            }
        }

        RefreshBatchAccents();
        RefreshDocumentGroups();
        return $"Auto-export finished: {exported} exported, {left} not exported, {failed} failed";
    }

    /// <summary>SHA-256 (hex) of a source file's raw bytes, streamed rather than loaded fully into memory
    /// — computed once per file at import time, before rasterize/OCR, for duplicate detection (see
    /// <see cref="CaptureDocument.ContentHash"/> and <see cref="WatchSettings.DuplicateImportBehavior"/>).</summary>
    private static async Task<string> ComputeContentHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private async Task ImportScannedPagesAsync(IReadOnlyList<ScannedPageInfo> pages, DocumentSource source)
    {
        try
        {
            StatusText = "Importing scanned pages…";
            await ImportPathsAsync(pages.Select(page => page.ImagePath).ToList(), source, manageBusy: false).ConfigureAwait(true);
        }
        finally
        {
            if (!_watchProcessing && _watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
    }

    private async Task RunPostIndexStepsAsync(CaptureDocument document, IReadOnlyList<IndexValue> indexValues, DocumentTypeDefinition profile)
    {
        if (_postIndexSteps.Count == 0)
            return;

        var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
        var context = new PostIndexContext
        {
            Document = document,
            Pages = pages,
            DocumentIndexValues = indexValues,
            Profile = profile
        };

        foreach (var step in _postIndexSteps)
        {
            try
            {
                await step.RunAsync(context).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Post-index step {step.GetType().Name} failed for document {document.Id}: {ex}");
            }
        }
    }

    /// <summary>Every page's already-built lattice for a document — used to build a button script's
    /// Document.Text. Assumes lattices
    /// already exist (built during import); a page with none simply isn't included, same as before this
    /// was extracted into its own method.</summary>
    private async Task<List<PageLattice>> LoadAllLatticesAsync(CaptureDocument document)
    {
        var lattices = new List<PageLattice>();
        for (var page = 1; page <= document.PageCount; page++)
        {
            var lattice = await _latticeStore.GetAsync(document.Id, page).ConfigureAwait(true);
            if (lattice is not null)
                lattices.Add(lattice);
        }

        return lattices;
    }

    private async Task<DocumentRow> CreateRowAsync(
        CaptureDocument document,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, Task<IReadOnlyList<IndexValue>>>? batchValuesCache = null)
    {
        var row = new DocumentRow(document);
        if (document.ProfileId is { } profileId)
        {
            var profile = FindDocumentType(profileId);
            if (profile is not null)
            {
                row.ConfidenceThreshold = profile.AutoReadyThreshold;
                row.Locale = profile.Locale;
                row.ProfileName = profile.Name;
            }
        }

        var values = await _indexes.GetAsync(document.Id).ConfigureAwait(false);
        if (values.Count > 0)
            row.SetDocumentIndexes(values);
        if (document.BatchId is { } batchId)
        {
            var batchValues = await (batchValuesCache is null
                ? _indexes.GetBatchAsync(batchId)
                : batchValuesCache.GetOrAdd(batchId, id => _indexes.GetBatchAsync(id))).ConfigureAwait(false);
            if (batchValues.Count > 0)
                row.SetBatchIndexes(batchValues);
        }

        return row;
    }
}
