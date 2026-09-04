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
    public ObservableCollection<DocumentGroupViewModel> DocumentGroups { get; } = [];

    public string SelectedDocumentsSummary
    {
        get
        {
            var count = GetActingRows().Count;
            return count == 1 ? "1 selected" : $"{count} selected";
        }
    }

    public bool HasMultipleSelectedDocuments => SelectedDocuments.Count > 1;

    /// <summary>Table mode's Trash toggle — swaps what <see cref="ReloadDocumentsAsync"/> loads into
    /// <see cref="MainViewModel.Documents"/> between the normal list and every soft-deleted document
    /// (<c>IDocumentStore.GetTrashedAsync</c>), reusing the same collection, grouping, and selection
    /// machinery rather than introducing a parallel Trash-specific view. While true, every normal
    /// bulk action's CanExecute is gated off (see CanActOnSelected/CanMergeSelectedDocuments/
    /// CanMarkReady/CanExport/CanExportAll) — only Restore/Delete permanently are available.</summary>
    // See RefreshSelectionDependentCommands, called from this property's own OnShowTrashChanged below —
    // no NotifyCanExecuteChangedFor attributes needed here for the same reason SelectedDocument has none.
    [ObservableProperty]
    private bool _showTrash;

    partial void OnShowTrashChanged(bool value)
    {
        SelectedDocuments.Clear();
        SelectedDocument = null;
        RefreshSelectionDependentCommands();
        _ = ReloadDocumentsAsync();
    }

    [RelayCommand]
    private void ToggleTrashView() => ShowTrash = !ShowTrash;

    private bool CanActOnSelected() => !IsBusy && !ShowTrash && GetActingRows().Count > 0;

    private bool CanActOnTrash() => !IsBusy && ShowTrash && GetActingRows().Count > 0;

    /// <summary>Undoes a soft delete — the document reappears in the normal list exactly as it was.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnTrash))]
    private async Task RestoreSelectedTrashAsync()
    {
        var rows = GetActingRows();
        if (rows.Count == 0)
            return;

        IsBusy = true;
        try
        {
            foreach (var row in rows)
                await _store.RestoreAsync(row.Id).ConfigureAwait(true);

            await ReloadDocumentsAsync().ConfigureAwait(true);
            StatusText = $"Restored {rows.Count} document(s)";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The real, permanent removal from Trash — unlike every other place a document gets
    /// removed in this app (see SoftDeleteAsync call sites elsewhere), this one has no undo, so it's
    /// the one deletion path that still confirms first.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnTrash))]
    private async Task PurgeSelectedTrashAsync()
    {
        var rows = GetActingRows();
        if (rows.Count == 0)
            return;

        if (_dialogs.Host is not { } host)
            return;

        var confirmed = await _confirm.ConfirmAsync(
            host,
            "Delete permanently?",
            $"This permanently deletes {rows.Count} document(s) from Trash, including their original files. This can't be undone.",
            confirmText: "Delete permanently",
            cancelText: "Cancel");
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            foreach (var row in rows)
                await _store.PurgeAsync(row.Id).ConfigureAwait(true);

            await ReloadDocumentsAsync().ConfigureAwait(true);
            StatusText = $"Permanently deleted {rows.Count} document(s)";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task ApplySelectedProfileAsync(IndexingProfile? profile)
    {
        var rows = GetActingRows();
        if (profile is null || rows.Count == 0)
            return;

        IsBusy = true;
        try
        {
            foreach (var row in rows)
                await ApplyProfileToRowAsync(row, profile).ConfigureAwait(true);
            if (SelectedDocument is not null && rows.Contains(SelectedDocument))
            {
                LoadReviewIndexes(SelectedDocument);
                // ApplyProfileToRowAsync can trigger automatic redaction (if the profile has it
                // enabled) via the post-index pipeline — reload candidates so a fresh detection shows
                // up immediately instead of only after reselecting the document.
                await LoadRedactionCandidatesAsync(SelectedDocument).ConfigureAwait(true);
                ApplyRedactionsCommand.NotifyCanExecuteChanged();
            }

            RefreshIndexHighlights();
            RefreshDocumentGroups();
            StatusText = $"Applied {profile.Name} to {rows.Count} document(s)";
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

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task RemoveSelectedAsync()
    {
        var rows = GetActingRows();
        if (rows.Count == 0)
            return;

        IsBusy = true;
        try
        {
            foreach (var row in rows)
            {
                await _store.SoftDeleteAsync(row.Id).ConfigureAwait(true);
                Documents.Remove(row);
                SelectedDocuments.Remove(row);
            }

            RefreshBatchAccents();
            RefreshDocumentGroups();

            if (IsPreviewMode)
            {
                // Avalonia's DataGrid clears its own SelectedItem on a deferred layout pass when the
                // currently-selected row is removed from ItemsSource — that pass can run *after* this
                // method returns and silently stomp SelectedDocument back to null. Posting at Loaded
                // priority runs after that pass settles, so our reselection wins instead of losing the race.
                // Guard against a second, unrelated change (e.g. a subsequent import) landing in between:
                // only apply this reselection if nothing else has touched SelectedDocument since.
                var expected = SelectedDocument;
                var next = Documents.FirstOrDefault();
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(SelectedDocument, expected))
                        SelectedDocument = next;
                }, DispatcherPriority.Loaded);
            }

            StatusText = $"Removed {rows.Count} document(s)";
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

    private bool CanMergeSelectedDocuments() => !IsBusy && !ShowTrash && GetActingRows().Count > 1;

    [RelayCommand(CanExecute = nameof(CanMergeSelectedDocuments))]
    private async Task MergeSelectedDocumentsAsync()
    {
        var rows = SelectedDocuments.ToList();
        if (rows.Count < 2)
            return;

        IsBusy = true;
        try
        {
            var targetRow = rows[0];
            var merged = await _pageManagement.MergeDocumentsAsync(rows.Select(row => row.Id).ToList())
                .ConfigureAwait(true);

            if (_lastManualBatch is { } openBatch
                && rows.Skip(1).Any(row => row.Document.BatchId == openBatch.Id))
            {
                _lastManualBatch = merged.BatchId is { } batchId
                    ? new CaptureBatch
                    {
                        Id = batchId,
                        Number = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true)
                    }
                    : null;
            }

            foreach (var absorbed in rows.Skip(1))
                Documents.Remove(absorbed);
            await RefreshDocumentRowInPlaceAsync(targetRow, merged).ConfigureAwait(true);

            SelectedDocuments.Clear();
            SelectedDocuments.Add(targetRow);
            SelectedDocument = targetRow;
            RefreshBatchAccents();
            RefreshDocumentGroups();
            StatusText = $"Merged {rows.Count} documents into {merged.PageCount} pages";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMarkReady))]
    private async Task MarkReadyAsync()
    {
        if (SelectedDocument is null)
            return;

        // This can now trigger automatic redaction (a cold Presidio start can take well over a
        // minute — see PresidioSidecarLauncher), so it needs the same busy indication as any other
        // potentially-slow action, not just an instantaneous status flip.
        IsBusy = true;
        try
        {
            var document = SelectedDocument.Document;
            document.Status = DocumentStatus.Ready;
            await _store.UpdateAsync(document);
            SelectedDocument.NotifyIndexes();
            StatusText = "Marked ready";

            // Reaching Ready by manual override should trigger the same post-index steps (redaction,
            // etc.) as reaching it automatically through indexing — otherwise a document only gets
            // those side effects depending on how it got to Ready, which isn't a distinction the user
            // meant to make.
            if (document.ProfileId is { } profileId
                && await _profileStore.GetAsync(profileId).ConfigureAwait(true) is { } profile)
            {
                var indexes = await _indexes.GetAsync(document.Id).ConfigureAwait(true);
                var batchValues = document.BatchId is { } batchId
                    ? await _indexes.GetBatchAsync(batchId).ConfigureAwait(true)
                    : [];
                await RunPostIndexStepsAsync(document, batchValues.Concat(indexes).ToList(), profile).ConfigureAwait(true);

                // RunPostIndexStepsAsync can trigger automatic redaction — reload candidates so a
                // fresh detection shows up immediately instead of only after reselecting the document.
                if (SelectedDocument?.Document.Id == document.Id)
                {
                    await LoadRedactionCandidatesAsync(SelectedDocument).ConfigureAwait(true);
                    RefreshIndexHighlights();
                    ApplyRedactionsCommand.NotifyCanExecuteChanged();
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Table mode's bulk counterpart to MarkReadyCommand — that one only ever acts on
    /// SelectedDocument, so from Table mode (typically multiple documents selected, none of them
    /// "open" for review) there was previously no way to mark anything ready at all. Deliberately mirrors
    /// MarkReadyAsync's own leniency: it overrides low-confidence flagging (never checked here, same as
    /// there) so the indexer can consciously accept a shaky-but-plausible batch without opening each
    /// document, but still skips a document with a missing mandatory field — marking that Ready would
    /// hide genuinely absent data, not just a confidence judgement call.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task MarkSelectedReadyAsync()
    {
        var rows = GetActingRows();
        if (rows.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var marked = 0;
            var skipped = 0;
            foreach (var row in rows)
            {
                var indexable = row.Indexes.Where(index => !index.HideFromIndexing && !index.IsReadOnly).ToList();
                if (indexable.Count == 0 || indexable.Any(index => index.IsMissing))
                {
                    skipped++;
                    continue;
                }

                var document = row.Document;
                document.Status = DocumentStatus.Ready;
                await _store.UpdateAsync(document).ConfigureAwait(true);
                row.NotifyIndexes();
                marked++;

                // Same as MarkReadyAsync: reaching Ready by bulk override should trigger the same
                // post-index steps (redaction, etc.) as reaching it any other way.
                if (document.ProfileId is { } profileId
                    && await _profileStore.GetAsync(profileId).ConfigureAwait(true) is { } profile)
                {
                    var indexes = await _indexes.GetAsync(document.Id).ConfigureAwait(true);
                    var batchValues = document.BatchId is { } batchId
                        ? await _indexes.GetBatchAsync(batchId).ConfigureAwait(true)
                        : [];
                    await RunPostIndexStepsAsync(document, batchValues.Concat(indexes).ToList(), profile).ConfigureAwait(true);
                }
            }

            if (SelectedDocument is not null && rows.Contains(SelectedDocument))
            {
                await LoadRedactionCandidatesAsync(SelectedDocument).ConfigureAwait(true);
                RefreshIndexHighlights();
                ApplyRedactionsCommand.NotifyCanExecuteChanged();
            }

            RefreshDocumentGroups();
            StatusText = skipped == 0
                ? $"Marked {marked} document(s) ready"
                : $"Marked {marked} document(s) ready — {skipped} skipped (missing a required field)";

            if (marked > 0)
                _toasts.ShowSuccess(StatusText);
            else
                _toasts.ShowError(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMarkReady() =>
        !IsBusy
        && !ShowTrash
        && SelectedDocument is not null
        && SelectedDocument.Indexes.Any(index => !index.HideFromIndexing && !index.IsReadOnly)
        && SelectedDocument.Indexes.Where(index => !index.HideFromIndexing && !index.IsReadOnly).All(index => !index.IsMissing);

    async partial void OnSelectedDocumentChanged(DocumentRow? value)
    {
        IsAddingManualRedaction = false;
        RefreshSelectionDependentCommands();
        LoadReviewIndexes(value);
        await LoadRedactionCandidatesAsync(value);
        ApplyRedactionsCommand.NotifyCanExecuteChanged();
        await LoadSelectedDocumentAsync(value);
    }

    /// <summary>Refreshes a document row's state after a page-management operation, mutating the
    /// existing <see cref="DocumentRow"/>/<see cref="CaptureDocument"/> in place rather than swapping in
    /// a new row instance. This is deliberate, not just an optimization: replacing the object at this
    /// row's index in <see cref="Documents"/> makes Avalonia's DataGrid lose track of which row was
    /// selected (it doesn't reliably preserve selection across an ItemsSource element replacement — a
    /// single deferred re-assertion of SelectedItem was tried here and lost the race against the
    /// DataGrid's own later correction, unlike the simpler item-removal case RemoveSelectedAsync already
    /// works around), silently bouncing the selection to another document right after the edit. Keeping
    /// the same object reference selected the whole time sidesteps that entirely.</summary>
    private async Task RefreshDocumentRowInPlaceAsync(DocumentRow row, CaptureDocument updated)
    {
        row.Document.StoredPath = updated.StoredPath;
        row.Document.PageCount = updated.PageCount;
        row.Document.Status = updated.Status;
        row.Document.RedactionStatus = updated.RedactionStatus;
        row.Document.RedactedPath = updated.RedactedPath;
        row.Document.ErrorMessage = updated.ErrorMessage;

        if (updated.ProfileId is { } profileId)
        {
            var profile = await _profileStore.GetAsync(profileId).ConfigureAwait(true);
            if (profile is not null)
            {
                row.ConfidenceThreshold = profile.AutoReadyThreshold;
                row.Locale = profile.Locale;
                row.ProfileName = profile.Name;
            }
        }

        var documentValues = await _indexes.GetAsync(row.Id).ConfigureAwait(true);
        row.SetDocumentIndexes(documentValues);
        if (row.Document.BatchId is { } batchId)
        {
            var batchValues = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
            row.SetBatchIndexes(batchValues);
        }

        row.NotifyIndexes();

        // Since the row object never changed, SelectedDocument never "changes" either, so
        // OnSelectedDocumentChanged's usual reload doesn't fire on its own — do the same reload it
        // would have done, directly, when this is the row currently being previewed.
        if (ReferenceEquals(SelectedDocument, row))
        {
            LoadReviewIndexes(row);
            await LoadRedactionCandidatesAsync(row).ConfigureAwait(true);
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
            await LoadSelectedDocumentAsync(row).ConfigureAwait(true);
        }
    }

    private async Task ApplyProfileToRowAsync(DocumentRow row, IndexingProfile profile)
    {
        await ApplyProfileToDocumentAsync(row.Document, profile).ConfigureAwait(true);
        row.ConfidenceThreshold = profile.AutoReadyThreshold;
        row.Locale = profile.Locale;
        row.ProfileName = profile.Name;
        var documentValues = await _indexes.GetAsync(row.Id).ConfigureAwait(true);
        row.SetDocumentIndexes(documentValues);
        await RefreshBatchRowsAsync(row.Document.BatchId).ConfigureAwait(true);
    }

    public async Task MoveDocumentToBatchAsync(Guid documentId, Guid batchId)
    {
        var row = Documents.FirstOrDefault(item => item.Id == documentId);
        if (row is null)
            return;

        var oldBatch = row.Document.BatchId;
        if (oldBatch == batchId)
            return;

        row.Document.BatchId = batchId;
        await _store.UpdateAsync(row.Document).ConfigureAwait(true);
        if (oldBatch is { } previous)
            await _store.DeleteEmptyBatchAsync(previous).ConfigureAwait(true);

        var batchValues = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
        row.SetBatchIndexes(batchValues);
        await _store.UpdateAsync(row.Document).ConfigureAwait(true);
        PlaceInBatch(row, batchId);
        RefreshBatchAccents();
        RefreshDocumentGroups();
        LoadReviewIndexes(row);
        RefreshIndexHighlights();
        StatusText = "Moved to another batch";
    }

    private async Task RefreshBatchRowsAsync(Guid? batchId)
    {
        if (batchId is not { } id)
            return;

        var batchValues = await _indexes.GetBatchAsync(id).ConfigureAwait(true);
        foreach (var row in Documents.Where(item => item.Document.BatchId == id))
        {
            row.SetBatchIndexes(batchValues);
            await _store.UpdateAsync(row.Document).ConfigureAwait(true);
        }
    }

    private void PlaceInBatch(DocumentRow row, Guid batchId)
    {
        Documents.Remove(row);
        var last = -1;
        for (var i = 0; i < Documents.Count; i++)
        {
            if (Documents[i].Document.BatchId == batchId)
                last = i;
        }

        if (last >= 0)
            Documents.Insert(last + 1, row);
        else
            Documents.Add(row);
    }

    private void RefreshBatchAccents()
    {
        Guid? previous = null;
        var accent = false;
        foreach (var row in Documents)
        {
            var batchId = row.Document.BatchId;
            if (batchId != previous)
            {
                if (previous is not null)
                    accent = !accent;
                previous = batchId;
            }

            row.BatchAccent = accent;
        }

        RefreshDuplicateFlags();
    }

    // Bundled into RefreshBatchAccents (called from it) rather than given its own separate call sites —
    // every place a document's presence/hash could plausibly have changed already calls
    // RefreshBatchAccents, so piggybacking here means IsDuplicate can never go stale the way a
    // separately-wired derived flag could if a new trigger point were missed.
    private void RefreshDuplicateFlags()
    {
        var groups = Documents
            .Where(row => !string.IsNullOrEmpty(row.Document.ContentHash))
            .GroupBy(row => row.Document.ContentHash!)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var row in Documents)
        {
            if (row.Document.ContentHash is { } hash && groups.TryGetValue(hash, out var duplicates))
            {
                var others = duplicates
                    .Where(item => item.Id != row.Id)
                    .Select(item => item.FileName)
                    .Distinct()
                    .ToList();
                row.IsDuplicate = true;
                row.DuplicateTooltip = others.Count == 1
                    ? $"Matches an already-imported document: {others[0]}"
                    : $"Matches {others.Count} other already-imported document(s)";
            }
            else
            {
                row.IsDuplicate = false;
                row.DuplicateTooltip = string.Empty;
            }
        }
    }

    private void RefreshDocumentGroups()
    {
        var groups = new List<DocumentGroupViewModel>();

        foreach (var byProfile in Documents.Where(row => row.Document.ProfileId is not null)
                     .GroupBy(row => row.Document.ProfileId!.Value))
        {
            var profile = Profiles.FirstOrDefault(item => item.Id == byProfile.Key);
            var documents = byProfile.OrderBy(row => row.FileName, StringComparer.OrdinalIgnoreCase).ToList();

            // Batch-level fields now come from BatchProfile.Fields, not this (IndexingProfile-keyed)
            // group's own profile.Fields — and a group here can span documents from several different
            // batches/BatchProfiles, so there's no single BatchProfile to read field names from at this
            // point. Derive them from what the documents' batches actually carry instead (same source
            // the "profile deleted" fallback below already used) rather than adding a BatchProfileId
            // column to CaptureBatch just to look the definition back up.
            var batchFieldNames = documents
                .SelectMany(row => row.BatchIndexes)
                .Where(value => !value.HideFromIndexing)
                .Select(value => value.FieldName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            IReadOnlyList<string> documentFieldNames = profile is not null
                ? profile.Fields
                    .Where(field => !field.HideFromIndexing)
                    .Select(field => field.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : documents
                    .SelectMany(row => row.DocumentIndexes)
                    .Where(value => !value.HideFromIndexing)
                    .Select(value => value.FieldName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            groups.Add(new DocumentGroupViewModel
            {
                Title = profile?.Name ?? "Unknown profile",
                IsUnassigned = false,
                BatchFieldNames = batchFieldNames,
                DocumentFieldNames = documentFieldNames,
                Documents = documents
            });
        }

        groups = groups.OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase).ToList();

        var unassigned = Documents.Where(row => row.Document.ProfileId is null)
            .OrderBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unassigned.Count > 0)
        {
            groups.Add(new DocumentGroupViewModel
            {
                Title = "No profile applied",
                IsUnassigned = true,
                BatchFieldNames = [],
                DocumentFieldNames = [],
                Documents = unassigned
            });
        }

        DocumentGroups.Clear();
        foreach (var group in groups)
            DocumentGroups.Add(group);
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNoDocuments));
        RefreshSelectionDependentCommands();
    }

    private void OnSelectedDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSelectionDependentCommands();
    }
}
