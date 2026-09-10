using Capture.Core.CaptureProfiles;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Scripting;
using Capture.Core.Store;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class MainViewModel
{
    // Reclassifies already-captured documents under a different profile — the only other way to
    // change a document's fields/type today is to delete and re-import it. Resolves to the built-in
    // Unsorted profile when nothing is selected in the picker, so applying "None" acts as a
    // declassify action (same shape as an ad hoc import with no profile chosen).
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task ApplyProfileToSelectedAsync()
    {
        var rows = GetActingRows();
        if (rows.Count == 0 || _dialogs.Host is not { } host || _store is not IOpenBatchStore batches)
            return;

        var profile = SelectedCaptureProfile ?? BuiltInCaptureProfiles.Unsorted;
        var confirmed = await _confirm.ConfirmAsync(
            host,
            $"Apply \"{profile.Name}\"?",
            $"This replaces the current classification and field values on {rows.Count} document(s) with those from \"{profile.Name}\", and moves them into a batch for that profile. This can't be undone.",
            confirmText: "Apply profile",
            cancelText: "Cancel");
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            var targetBatch = await batches.GetOpenBatchAsync(profile.Id, "manual").ConfigureAwait(true);
            var batchValuesComputed = targetBatch is not null;
            targetBatch ??= await batches.CreateScopedBatchAsync(profile.Id, "manual").ConfigureAwait(true);
            var batchValues = batchValuesComputed
                ? await _indexes.GetBatchAsync(targetBatch.Id).ConfigureAwait(true)
                : [];

            foreach (var row in rows)
            {
                var document = row.Document;
                var oldBatchId = document.BatchId;
                var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
                var lattices = await LoadLatticesAsync(document.Id, pages).ConfigureAwait(true);

                // Batch-level fields only need computing once per action, and only for a batch this
                // action just created — an already-open batch's values were already resolved by
                // whatever put it in that state, and applying to more documents shouldn't clobber them.
                if (!batchValuesComputed)
                {
                    batchValues = await _profileApplicator.ApplyAsync(
                        profile.Batch.Fields, profile.Batch.Scripts, profile.Batch.SharedScriptSource, lattices, profile.Name,
                        context: new DefaultValueContext { ScriptScope = ScriptScopeKind.Batch },
                        pages: pages).ConfigureAwait(true);
                    await _indexes.SaveBatchAsync(targetBatch.Id, batchValues).ConfigureAwait(true);
                    batchValuesComputed = true;
                }

                var type = CapturePlanner.MatchDocumentType(profile, lattices);
                document.BatchId = targetBatch.Id;

                IReadOnlyList<IndexValue> values = [];
                if (type is not null)
                {
                    var batchNumber = await _store.GetBatchNumberAsync(targetBatch.Id).ConfigureAwait(true);
                    var documentNumber = await _store.GetDocumentNumberInBatchAsync(targetBatch.Id, document.Id).ConfigureAwait(true);
                    values = await _profileApplicator.ApplyAsync(
                        type,
                        lattices,
                        context: new DefaultValueContext
                        {
                            BatchNumber = batchNumber,
                            DocumentNumber = documentNumber,
                            ScriptScope = ScriptScopeKind.Document,
                            DocumentType = type.Name,
                            BatchValues = batchValues
                        },
                        pages: pages,
                        document: document).ConfigureAwait(true);
                }

                await _indexes.SaveAsync(document.Id, values).ConfigureAwait(true);
                document.ProfileId = type?.Id;
                document.Status = type is not null
                    ? IndexFormat.StatusFor(batchValues.Concat(values), type.AutoReadyThreshold)
                    : DocumentStatus.NeedsReview;
                await _store.UpdateAsync(document).ConfigureAwait(true);
                if (oldBatchId is { } previousBatchId && previousBatchId != targetBatch.Id)
                    await _store.DeleteEmptyBatchAsync(previousBatchId).ConfigureAwait(true);

                row.SetBatchIndexes(batchValues);
                row.SetDocumentIndexes(values);
                PlaceInBatch(row, targetBatch.Id);
            }

            RefreshBatchAccents();
            RefreshDocumentGroups();
            if (SelectedDocument is { } selected && rows.Contains(selected))
            {
                LoadReviewIndexes(selected);
                RefreshIndexHighlights();
            }

            StatusText = $"Applied \"{profile.Name}\" to {rows.Count} document(s)";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Apply profile failed: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<List<PageLattice>> LoadLatticesAsync(Guid documentId, IReadOnlyList<DocumentPage> pages)
    {
        var lattices = new List<PageLattice>();
        foreach (var page in pages)
        {
            var lattice = await _latticeStore.GetAsync(documentId, page.PageNumber).ConfigureAwait(true);
            if (lattice is not null)
                lattices.Add(lattice);
        }

        return lattices;
    }
}
