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
    public ObservableCollection<IndexValueRow> ReviewBatchIndexes { get; } = [];

    public ObservableCollection<IndexValueRow> ReviewDocumentIndexes { get; } = [];

    public bool HasReviewIndexes => ReviewBatchIndexes.Count > 0 || ReviewDocumentIndexes.Count > 0;

    public bool HasReviewBatchIndexes => ReviewBatchIndexes.Count > 0;

    public bool HasReviewDocumentIndexes => ReviewDocumentIndexes.Count > 0;

    [ObservableProperty]
    private IReadOnlyList<IndexHighlight> _indexHighlights = [];

    [ObservableProperty]
    private IndexValueRow? _selectedIndex;

    [RelayCommand]
    private void SelectIndexHighlight(Guid id)
    {
        var row = ReviewBatchIndexes.Concat(ReviewDocumentIndexes)
            .FirstOrDefault(item => item.Value.FieldId == id);
        if (row is not null)
        {
            SelectedIndex = row;
            return;
        }

        var candidateRow = RedactionCandidates.FirstOrDefault(item => item.Id == id);
        if (candidateRow is not null)
        {
            SelectedRedactionCandidate = candidateRow;
            RefreshIndexHighlights();
        }
    }

    partial void OnSelectedIndexChanged(IndexValueRow? value)
    {
        // Selecting an index field (via a click on its row or on its highlight in the preview) and a
        // redaction candidate are mutually exclusive — only one thing is ever "the selected thing".
        if (value is not null)
            SelectedRedactionCandidate = null;
        RefreshRowSelectionFlags();

        if (value is not null && value.Value.PageNumber >= 1 && value.Value.PageNumber != CurrentPageNumber)
        {
            CurrentPageNumber = value.Value.PageNumber;
            _ = ShowPageAsync();
            return;
        }

        RefreshIndexHighlights();
    }

    private void LoadReviewIndexes(DocumentRow? row)
    {
        ClearReview(ReviewBatchIndexes);
        ClearReview(ReviewDocumentIndexes);
        SelectedIndex = null;

        if (row is null)
        {
            OnPropertyChanged(nameof(HasReviewIndexes));
            OnPropertyChanged(nameof(HasReviewBatchIndexes));
            OnPropertyChanged(nameof(HasReviewDocumentIndexes));
            return;
        }

        foreach (var value in row.BatchIndexes.Where(index => !index.HideFromIndexing))
            ReviewBatchIndexes.Add(CreateReviewRow(row, value));
        foreach (var value in row.DocumentIndexes.Where(index => !index.HideFromIndexing))
            ReviewDocumentIndexes.Add(CreateReviewRow(row, value));

        OnPropertyChanged(nameof(HasReviewIndexes));
        OnPropertyChanged(nameof(HasReviewBatchIndexes));
        OnPropertyChanged(nameof(HasReviewDocumentIndexes));
        MarkReadyCommand.NotifyCanExecuteChanged();
    }

    private IndexValueRow CreateReviewRow(DocumentRow document, IndexValue value)
    {
        var row = new IndexValueRow(value, document.ConfidenceThreshold, document.Locale, _scripts?.IsAvailable ?? false)
        {
            Changed = () => _ = PersistReviewAsync(document)
        };
        row.Selected = () => SelectedIndex = row;
        return row;
    }

    /// <summary>Runs a Button field's attached script — the review panel's on-demand counterpart to
    /// AfterFieldsPopulated profile scripts. Full read/write over every field on the document (unlike a
    /// Script-kind field's own read-only expression), gated on WatchSettings.AllowFieldScripts exactly
    /// like real import/export, since this is running someone else's (the profile author's) script for
    /// whoever happens to be reviewing, not the author testing their own work interactively — that
    /// interactive exception is the Designer's "Run test" only.</summary>
    [RelayCommand]
    private async Task RunButtonFieldAsync(IndexValueRow row)
    {
        if (SelectedDocument is not { } documentRow || row.Value.Kind != FieldKind.Button)
            return;

        if (_scripts is null || !_scripts.IsAvailable)
        {
            StatusText = "Scripting is off — turn on \"Allow profile scripts\" in Settings";
            _toasts.ShowError(StatusText);
            return;
        }

        var document = documentRow.Document;
        var profile = document.ProfileId is { } profileId ? Profiles.FirstOrDefault(item => item.Id == profileId) : null;
        var field = profile?.Fields.FirstOrDefault(item => item.Id == row.Value.FieldId);
        if (field is null || string.IsNullOrWhiteSpace(field.ButtonScriptSource))
        {
            StatusText = "This button has no script configured";
            _toasts.ShowError(StatusText);
            return;
        }

        row.IsRunning = true;
        try
        {
            var lattices = await LoadAllLatticesAsync(document).ConfigureAwait(true);
            DefaultValueContext? defaultContext = null;
            if (document.BatchId is { } batchId)
            {
                defaultContext = new DefaultValueContext
                {
                    BatchNumber = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true),
                    DocumentNumber = await _store.GetDocumentNumberInBatchAsync(batchId, document.Id).ConfigureAwait(true),
                    Timestamp = DateTimeOffset.Now
                };
            }

            var context = new ScriptExecutionContext
            {
                ProfileName = profile!.Name,
                DocumentNumber = defaultContext?.DocumentNumber ?? 1,
                BatchNumber = defaultContext?.BatchNumber ?? 1,
                Timestamp = DateTimeOffset.Now,
                Values = documentRow.Indexes,
                Document = ScriptDocumentInfo.From(lattices, document)
            };

            // The real field's Id, not a fresh Guid — so RoslynFieldScriptRunner's compiled-script
            // cache (keyed on id + source hash) is actually reused across repeated clicks.
            var script = new FieldScript
            {
                Id = field.Id,
                Name = field.Name,
                Source = field.ButtonScriptSource,
                TimeoutSeconds = field.ButtonTimeoutSeconds
            };

            var result = await _scripts.RunProfileScriptAsync(script, context, sharedSource: profile.SharedScriptSource).ConfigureAwait(true);
            if (!result.Success)
            {
                Trace.TraceError($"Button script \"{field.Name}\" failed: {result.ErrorMessage}");
                StatusText = $"Script failed: {result.ErrorMessage}";
                _toasts.ShowError(StatusText);
                return;
            }

            await PersistReviewAsync(documentRow).ConfigureAwait(true);
            // A button script can write to any field, not just its own — refresh every currently
            // visible row's cached display state rather than tracking which ones actually changed.
            foreach (var visibleRow in ReviewBatchIndexes.Concat(ReviewDocumentIndexes))
                visibleRow.Refresh();
            StatusText = "Script ran successfully";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            row.IsRunning = false;
        }
    }

    /// <summary>Copies one Document-level field's current value into the same field on every other
    /// currently-selected document — for correcting/setting a shared value (e.g. "Supplier") across a
    /// batch of documents without opening each one individually. Batch-level fields are excluded: they
    /// already propagate to every document in their batch automatically via PersistReviewAsync, so
    /// "applying to the selection" would be redundant (and ambiguous across documents from different
    /// batches).</summary>
    [RelayCommand]
    private async Task ApplyFieldToSelectionAsync(IndexValueRow row)
    {
        if (SelectedDocument is not { } source || row.IsBatch)
            return;

        var targets = SelectedDocuments.Where(item => item.Id != source.Id).ToList();
        if (targets.Count == 0)
            return;

        var applied = 0;
        foreach (var target in targets)
        {
            var match = target.DocumentIndexes.FirstOrDefault(item => item.FieldId == row.Value.FieldId);
            if (match is null)
                continue;

            match.Value = row.Value.Value;
            match.IsManual = true;
            match.Confidence = 100;
            match.ValidationError = IndexFormat.Validate(match.Value, match.Format, target.Locale);
            await PersistReviewAsync(target).ConfigureAwait(true);
            applied++;
        }

        StatusText = applied > 0
            ? $"Applied \"{row.Name}\" to {applied} other document{(applied == 1 ? "" : "s")}"
            : "No other selected documents have this field";
        if (applied > 0)
            _toasts.ShowSuccess(StatusText);
        else
            _toasts.ShowError(StatusText);
    }

    private static void ClearReview(ObservableCollection<IndexValueRow> rows)
    {
        foreach (var item in rows)
            item.Changed = null;
        rows.Clear();
    }

    private async Task PersistReviewAsync(DocumentRow row)
    {
        try
        {
            await _indexes.SaveAsync(row.Id, row.DocumentIndexes).ConfigureAwait(true);
            if (row.Document.BatchId is { } batchId)
            {
                await _indexes.SaveBatchAsync(batchId, row.BatchIndexes).ConfigureAwait(true);
                foreach (var other in Documents.Where(item => item.Document.BatchId == batchId && item.Id != row.Id))
                {
                    other.SetBatchIndexes(row.BatchIndexes);
                    await _store.UpdateAsync(other.Document).ConfigureAwait(true);
                }
            }

            row.RecalcStatus();
            await _store.UpdateAsync(row.Document).ConfigureAwait(true);
            row.NotifyIndexes();
            RefreshIndexHighlights();
            MarkReadyCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void RefreshIndexHighlights()
    {
        var indexHighlights = ReviewBatchIndexes.Concat(ReviewDocumentIndexes)
            .Where(item => item.Value.Bounds is not null && item.Value.PageNumber == CurrentPageNumber)
            .Select(item => new IndexHighlight
            {
                FieldId = item.Value.FieldId,
                FieldName = item.Value.FieldName,
                X = item.Value.Bounds!.X,
                Y = item.Value.Bounds.Y,
                Width = item.Value.Bounds.Width,
                Height = item.Value.Bounds.Height,
                IsSelected = SelectedIndex?.Value.FieldId == item.Value.FieldId,
                CanEdit = false
            });

        var redactionHighlights = RedactionCandidates
            .Where(row => row.PageNumber == CurrentPageNumber)
            .Select(row => new IndexHighlight
            {
                FieldId = row.Id,
                FieldName = row.Label,
                X = row.Candidate.X,
                Y = row.Candidate.Y,
                Width = row.Candidate.Width,
                Height = row.Candidate.Height,
                IsSelected = SelectedRedactionCandidate?.Id == row.Id,
                CanEdit = row.IsManual,
                IsRedaction = true,
                IsRejected = !row.IsConfirmed
            });

        IndexHighlights = indexHighlights.Concat(redactionHighlights).ToList();
    }
}
