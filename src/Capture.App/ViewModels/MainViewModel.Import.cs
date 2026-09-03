using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Security.Cryptography;
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
    // Trash-aware: reloads whichever list is currently showing (see ShowTrash in
    // MainViewModel.Documents.cs), so every existing caller (startup, auto-cleanup, Settings save)
    // correctly refreshes the Trash view instead of silently switching back to the normal list if
    // that's what the reviewer happened to be looking at.
    private async Task ReloadDocumentsAsync()
    {
        var documents = ShowTrash
            ? await _store.GetTrashedAsync().ConfigureAwait(true)
            : await _store.GetAllAsync().ConfigureAwait(true);
        Documents.Clear();
        SelectedDocuments.Clear();
        foreach (var document in documents)
            Documents.Add(await CreateRowAsync(document).ConfigureAwait(true));
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
                return;
            }

            await ImportPathsAsync(files);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportPathsAsync(
        IReadOnlyList<string> paths,
        DocumentSource source = DocumentSource.Import,
        IndexingProfile? profile = null,
        string? watchRoot = null,
        WatchFolderEntry? watchFolderEntry = null,
        IReadOnlyDictionary<string, int>? imageDpiByPath = null,
        bool manageBusy = true)
    {
        if (manageBusy)
            IsBusy = true;
        try
        {
            profile ??= SelectedImportProfile;

            var selectedBatchProfile = watchFolderEntry is not null
                ? (watchFolderEntry.BatchProfileId is { } bpId
                    ? BatchProfiles.FirstOrDefault(item => item.Id == bpId)
                    : null)
                : SelectedBatchProfile;
            var batchProfile = BatchProfileResolver.Resolve(selectedBatchProfile, _watchSettings.NoBatchProfileBehavior);
            var keepsBatchOpen = batchProfile is null || batchProfile.Trigger == BatchTrigger.Manual;
            var resumeBatch = watchFolderEntry is null && keepsBatchOpen
                ? _lastManualBatch
                : null;
            var allocator = await BatchAllocator.CreateAsync(
                _store, batchProfile, watchFolderEntry?.Id, resumeBatch).ConfigureAwait(true);

            var index = 0;
            DocumentRow? last = null;
            var batchSources = new Dictionary<Guid, CaptureDocument>();
            var batchSeparatorValues = new Dictionary<Guid, string?>();
            var failedFiles = 0;
            var skippedDuplicates = 0;
            foreach (var path in paths)
            {
                index++;
                StatusText = $"Importing {index} of {paths.Count}: {Path.GetFileName(path)}";
                try
                {
                    var contentHash = await ComputeContentHashAsync(path).ConfigureAwait(true);
                    if (_watchSettings.DuplicateImportBehavior == DuplicateImportBehavior.Skip
                        && (await _store.FindByContentHashAsync(contentHash).ConfigureAwait(true)).Count > 0)
                    {
                        skippedDuplicates++;
                        MoveWatchFile(path, watchRoot, watchFolderEntry, success: true);
                        continue;
                    }

                    var dpi = imageDpiByPath?.GetValueOrDefault(path);
                    var imported = await _importer.ImportAsync(
                            path, source, profile, batchProfile, imageDpiOverride: dpi)
                        .ConfigureAwait(true);
                    var (fileLast, failed) = await MaterializeImportedAsync(
                            imported, profile, allocator, batchSources, batchSeparatorValues, isFirstOfFile: true, contentHash)
                        .ConfigureAwait(true);
                    if (fileLast is not null)
                        last = fileLast;

                    if (imported.Count == 0 || failed)
                        failedFiles++;
                    MoveWatchFile(path, watchRoot, watchFolderEntry, success: imported.Count > 0 && !failed);
                }
                catch (Exception ex)
                {
                    failedFiles++;
                    StatusText = ex.Message;
                    MoveWatchFile(path, watchRoot, watchFolderEntry, success: false);
                }
            }

            if (profile is not null)
            {
                foreach (var (batchId, batchSource) in batchSources)
                {
                    await ApplyBatchFieldsAsync(batchSource, profile, batchSeparatorValues.GetValueOrDefault(batchId))
                        .ConfigureAwait(true);
                    await RefreshBatchRowsAsync(batchId).ConfigureAwait(true);
                }
            }

            if (watchFolderEntry is null && keepsBatchOpen)
                _lastManualBatch = allocator.Current;

            RefreshBatchAccents();
            RefreshDocumentGroups();
            if (last is not null)
            {
                SelectedDocument = last;
                // SelectedDocument's setter kicks off LoadSelectedDocumentAsync via an async-void
                // On...Changed handler, which this method does not otherwise wait for. Without this
                // explicit await, IsBusy (which gates page-navigation commands) can flip back to
                // false in the finally below while that background load is still in flight, leaving
                // Previous/NextPage's CanExecute settled against a stale PageCount for this document.
                await LoadSelectedDocumentAsync(last).ConfigureAwait(true);
            }

            var imports = paths.Count - skippedDuplicates;
            var succeeded = imports - failedFiles;
            var suffix = skippedDuplicates > 0 ? $" — {skippedDuplicates} skipped as duplicate" : string.Empty;
            StatusText = imports == 0
                ? $"All {paths.Count} file(s) skipped as duplicates"
                : failedFiles == 0
                    ? $"Imported {succeeded} file(s){suffix}"
                    : failedFiles == imports
                        ? $"Import failed for all {imports} file(s){suffix}"
                        : $"Imported {succeeded} of {imports} file(s) — {failedFiles} failed{suffix}";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (manageBusy)
                IsBusy = false;
            if (manageBusy && !_watchProcessing && _watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
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

    /// <summary>Batch-allocates, applies profile fields to, and creates a row for each document produced
    /// by one import call — shared by <see cref="ImportPathsAsync"/>'s per-file loop and
    /// <see cref="ImportScannedPagesAsync"/>'s single scan-job import, so the two entry points can't
    /// drift apart on how a resulting <see cref="ImportedDocument"/> gets surfaced.</summary>
    /// <param name="contentHash">The source file's hash, if known — see <see cref="ComputeContentHashAsync"/>.
    /// Only stamped onto the resulting document when the import produced exactly one document from it;
    /// when a single source file is split into several, none of them individually equals the whole
    /// file's bytes, so tagging them all with the same hash would falsely flag them as duplicates of
    /// each other.</param>
    private async Task<(DocumentRow? Last, bool Failed)> MaterializeImportedAsync(
        IReadOnlyList<ImportedDocument> imported,
        IndexingProfile? profile,
        BatchAllocator allocator,
        Dictionary<Guid, CaptureDocument> batchSources,
        Dictionary<Guid, string?> batchSeparatorValues,
        bool isFirstOfFile,
        string? contentHash = null)
    {
        DocumentRow? last = null;
        var failed = false;
        foreach (var item in imported)
        {
            var document = item.Document;
            var batch = await allocator.NextAsync(isFirstOfFile, item.StartsNewBatch, document.PageCount)
                .ConfigureAwait(true);
            isFirstOfFile = false;

            document.BatchId = batch.Id;
            document.ContentHash = imported.Count == 1 ? contentHash : null;
            await _store.UpdateAsync(document).ConfigureAwait(true);
            await ApplyDocumentFieldsAsync(document, profile, item.SeparatorValues, item.BatchSeparatorValue)
                .ConfigureAwait(true);
            if (!batchSources.ContainsKey(batch.Id) && document.Status != DocumentStatus.Error)
            {
                batchSources[batch.Id] = document;
                batchSeparatorValues[batch.Id] = item.BatchSeparatorValue;
            }
            last = await CreateRowAsync(document).ConfigureAwait(true);
            Documents.Add(last);
            if (document.Status == DocumentStatus.Error)
                failed = true;
        }

        return (last, failed);
    }

    private async Task ImportScannedPagesAsync(IReadOnlyList<ScannedPageInfo> pages, DocumentSource source)
    {
        try
        {
            var profile = SelectedImportProfile;
            var batchProfile = BatchProfileResolver.Resolve(SelectedBatchProfile, _watchSettings.NoBatchProfileBehavior);
            var keepsBatchOpen = batchProfile is null || batchProfile.Trigger == BatchTrigger.Manual;
            var resumeBatch = keepsBatchOpen ? _lastManualBatch : null;
            var allocator = await BatchAllocator.CreateAsync(_store, batchProfile, watchFolderEntryId: null, resumeBatch)
                .ConfigureAwait(true);

            var batchSources = new Dictionary<Guid, CaptureDocument>();
            var batchSeparatorValues = new Dictionary<Guid, string?>();
            StatusText = "Importing scanned pages…";

            IReadOnlyList<ImportedDocument> imported;
            try
            {
                imported = await _importer.ImportScannedPagesAsync(pages, source, profile, batchProfile)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusText = $"Scan import failed: {ex.Message}";
                return;
            }

            var (last, failed) = await MaterializeImportedAsync(
                    imported, profile, allocator, batchSources, batchSeparatorValues, isFirstOfFile: true)
                .ConfigureAwait(true);

            if (profile is not null)
            {
                foreach (var (batchId, batchSource) in batchSources)
                {
                    await ApplyBatchFieldsAsync(batchSource, profile, batchSeparatorValues.GetValueOrDefault(batchId))
                        .ConfigureAwait(true);
                    await RefreshBatchRowsAsync(batchId).ConfigureAwait(true);
                }
            }

            if (keepsBatchOpen)
                _lastManualBatch = allocator.Current;

            RefreshBatchAccents();
            RefreshDocumentGroups();
            if (last is not null)
            {
                SelectedDocument = last;
                await LoadSelectedDocumentAsync(last).ConfigureAwait(true);
            }

            StatusText = imported.Count == 0
                ? "Scan produced no pages"
                : failed
                    ? "Scan imported with errors"
                    : $"Imported {imported.Count} document(s) from scan";
        }
        finally
        {
            if (!_watchProcessing && _watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
    }

    private async Task ApplyProfileToDocumentAsync(
        CaptureDocument document,
        IndexingProfile profile,
        bool extractBatch)
    {
        await ApplyDocumentFieldsAsync(document, profile).ConfigureAwait(true);
        if (extractBatch)
            await ApplyBatchFieldsAsync(document, profile).ConfigureAwait(true);
    }

    private async Task ApplyDocumentFieldsAsync(
        CaptureDocument document,
        IndexingProfile? profile,
        IReadOnlyDictionary<Guid, string>? separatorValues = null,
        string? batchSeparatorValue = null)
    {
        if (profile is null || document.Status == DocumentStatus.Error)
            return;

        var extracted = await ExtractAsync(document, profile, batchSeparatorValue).ConfigureAwait(true);
        if (separatorValues is { Count: > 0 })
        {
            foreach (var value in extracted)
            {
                if (!string.IsNullOrWhiteSpace(value.Value) || !separatorValues.TryGetValue(value.FieldId, out var seeded))
                    continue;
                value.Value = seeded;
                value.Confidence = Math.Max(value.Confidence, 95);
            }
        }

        var documentValues = extracted.Where(value => value.Level != IndexLevel.Batch).ToList();
        await _indexes.SaveAsync(document.Id, documentValues).ConfigureAwait(true);
        document.ProfileId = profile.Id;
        var batchValues = document.BatchId is { } batchId
            ? await _indexes.GetBatchAsync(batchId).ConfigureAwait(true)
            : [];
        document.Status = IndexFormat.StatusFor(batchValues.Concat(documentValues), profile.AutoReadyThreshold);
        await _store.UpdateAsync(document).ConfigureAwait(true);
        await RunPostIndexStepsAsync(document, batchValues.Concat(documentValues).ToList(), profile).ConfigureAwait(true);
    }

    private async Task RunPostIndexStepsAsync(CaptureDocument document, IReadOnlyList<IndexValue> indexValues, IndexingProfile profile)
    {
        if (_postIndexSteps.Count == 0)
            return;

        var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
        var context = new PostIndexContext
        {
            Document = document,
            Pages = pages,
            IndexValues = indexValues,
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

    private async Task ApplyBatchFieldsAsync(CaptureDocument document, IndexingProfile profile, string? batchSeparatorValue = null)
    {
        if (document.BatchId is not { } batchId || document.Status == DocumentStatus.Error)
            return;

        var extracted = await ExtractAsync(document, profile, batchSeparatorValue).ConfigureAwait(true);
        var batchValues = extracted.Where(value => value.Level == IndexLevel.Batch).ToList();

        if (string.IsNullOrEmpty(batchSeparatorValue))
        {
            // No freshly captured batch-trigger value this time (e.g. a manual "apply profile" re-run,
            // which has no barcode/regex hit to seed from). A BatchSeparatorValue field has no other way
            // to derive its value, so preserve whatever a real import already captured for it rather than
            // blanking it out — but only that field kind: any other batch-level field (zone/pattern-based)
            // can legitimately re-extract as empty, and that new result should win, not a stale one.
            var separatorFieldIds = profile.Fields
                .Where(field => field.Kind == FieldKind.BatchSeparatorValue)
                .Select(field => field.Id)
                .ToHashSet();
            if (separatorFieldIds.Count > 0)
            {
                var existing = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
                foreach (var value in batchValues)
                {
                    if (separatorFieldIds.Contains(value.FieldId)
                        && string.IsNullOrWhiteSpace(value.Value)
                        && existing.FirstOrDefault(item => item.FieldId == value.FieldId) is { } previous
                        && !string.IsNullOrWhiteSpace(previous.Value))
                        value.Value = previous.Value;
                }
            }
        }

        await _indexes.SaveBatchAsync(batchId, batchValues).ConfigureAwait(true);
    }

    /// <summary>Every page's already-built lattice for a document — used both for real extraction
    /// (ExtractAsync) and to build a script's Document.Text (RunButtonFieldAsync). Assumes lattices
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

    private async Task<IReadOnlyList<IndexValue>> ExtractAsync(
        CaptureDocument document,
        IndexingProfile profile,
        string? batchSeparatorValue = null)
    {
        var lattices = await LoadAllLatticesAsync(document).ConfigureAwait(true);

        DefaultValueContext? context = null;
        var existingValues = new List<IndexValue>(await _indexes.GetAsync(document.Id).ConfigureAwait(true));
        if (document.BatchId is { } batchId)
        {
            context = new DefaultValueContext
            {
                BatchNumber = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true),
                DocumentNumber = await _store.GetDocumentNumberInBatchAsync(batchId, document.Id).ConfigureAwait(true),
                Timestamp = DateTimeOffset.Now
            };
            existingValues.AddRange(await _indexes.GetBatchAsync(batchId).ConfigureAwait(true));
        }

        var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
        return await _applicator.ApplyAsync(profile, lattices, context, pages, batchSeparatorValue, existingValues, document)
            .ConfigureAwait(true);
    }

    private async Task<DocumentRow> CreateRowAsync(CaptureDocument document)
    {
        var row = new DocumentRow(document);
        if (document.ProfileId is { } profileId)
        {
            var profile = await _profileStore.GetAsync(profileId).ConfigureAwait(true);
            if (profile is not null)
            {
                row.ConfidenceThreshold = profile.AutoReadyThreshold;
                row.Locale = profile.Locale;
                row.ProfileName = profile.Name;
            }
        }

        var values = await _indexes.GetAsync(document.Id).ConfigureAwait(true);
        if (values.Count > 0)
            row.SetDocumentIndexes(values);
        if (document.BatchId is { } batchId)
        {
            var batchValues = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
            if (batchValues.Count > 0)
                row.SetBatchIndexes(batchValues);
        }

        return row;
    }
}
