using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
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
    private enum ExportOutcome { Exported, ExportedAndRemoved, AlreadyExported, NotReady, NoExport, Failed }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var selected = GetActingRows();
        await RunExportAsync(selected.Count > 0 ? selected : Documents.ToList()).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanExportAll))]
    private async Task ExportAllAsync() => await RunExportAsync(Documents.ToList()).ConfigureAwait(true);

    private bool CanExport() => !IsBusy && !ShowTrash && Documents.Count > 0;

    private bool CanExportAll() => !IsBusy && !ShowTrash && Documents.Count > 0;

    private async Task RunExportAsync(IReadOnlyList<DocumentRow> rows)
    {
        if (rows.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var exported = 0;
            var removed = 0;
            var failed = 0;
            var alreadyExported = 0;
            var notReady = 0;
            var noExport = 0;
            string? firstError = null;
            foreach (var row in rows)
            {
                var (outcome, error) = await ExportDocumentAsync(row).ConfigureAwait(true);
                firstError ??= error;
                switch (outcome)
                {
                    case ExportOutcome.Exported:
                        exported++;
                        break;
                    case ExportOutcome.ExportedAndRemoved:
                        removed++;
                        break;
                    case ExportOutcome.Failed:
                        failed++;
                        break;
                    case ExportOutcome.AlreadyExported:
                        alreadyExported++;
                        break;
                    case ExportOutcome.NotReady:
                        notReady++;
                        break;
                    case ExportOutcome.NoExport:
                        noExport++;
                        break;
                }
            }

            RefreshBatchAccents();
            RefreshDocumentGroups();

            // A removed row can leave SelectedDocument dangling on a document that's no longer in
            // Documents — same DataGrid-deferred-clear race RemoveSelectedAsync already guards against.
            if (removed > 0 && IsPreviewMode && SelectedDocument is not null && !Documents.Contains(SelectedDocument))
            {
                var expected = SelectedDocument;
                var next = Documents.FirstOrDefault();
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(SelectedDocument, expected))
                        SelectedDocument = next;
                }, DispatcherPriority.Loaded);
            }

            var parts = new List<string>();
            if (exported > 0)
                parts.Add($"exported {exported}");
            if (removed > 0)
                parts.Add($"exported and removed {removed}");
            if (failed > 0)
                parts.Add($"{failed} failed — {firstError}");
            if (alreadyExported > 0)
                parts.Add($"{alreadyExported} already exported (Mark ready to send again)");
            if (notReady > 0)
                parts.Add($"{notReady} not ready");
            if (noExport > 0)
                parts.Add($"{noExport} with no export set up");
            StatusText = parts.Count == 0 ? "Nothing to export" : string.Join(", ", parts);
            StatusIsError = failed > 0;
            if (failed > 0)
                _toasts.ShowError(StatusText);
            else if (exported > 0 || removed > 0)
                _toasts.ShowSuccess(StatusText);
            else
                // Nothing went anywhere — say why, rather than leaving the click looking ignored.
                _toasts.ShowInfo($"Nothing exported: {StatusText}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Kept even when an export failed, so the history shows the attempt and why.
    private async Task RecordExportsAsync(DocumentRow row, DocumentTypeDefinition profile, IReadOnlyList<ExportResult> results)
    {
        if (results.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var records = results.Select(result =>
        {
            var definition = profile.Exports.FirstOrDefault(export => export.Id == result.ExportId);
            return new ExportRecord(
                result.ExportId,
                definition?.Name ?? "Export",
                definition?.Type ?? ExportType.None,
                now,
                result.Success,
                result.Reference,
                result.Success ? null : result.Message);
        }).ToList();
        try
        {
            if (_exportHistory is not null)
                await _exportHistory.AddAsync(row.Id, records).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The export itself happened; failing to note it down mustn't turn it into a failure.
            Trace.TraceError($"Could not record the export history for document {row.Id}: {ex}");
        }
        row.AddExportRecords(records);
    }

    // Only a Ready document is eligible — one that's still NeedsReview, Queued, Processing, or Error
    // is skipped rather than exported with incomplete/unvalidated data. An already-Exported document is
    // also skipped, and reported as such: Mark ready re-opens it for a deliberate second export.
    // Error: why it failed — the first failing export's message, which names that export.
    private async Task<(ExportOutcome Outcome, string? Error)> ExportDocumentAsync(DocumentRow row)
    {
        var document = row.Document;
        if (document.Status == DocumentStatus.Exported)
            return (ExportOutcome.AlreadyExported, null);
        if (document.Status != DocumentStatus.Ready)
            return (ExportOutcome.NotReady, null);

        var profile = FindDocumentType(document.ProfileId);
        if (profile is null || profile.Exports.Count(item => item.Enabled) == 0)
            return (ExportOutcome.NoExport, null);

        var batchFields = FindCaptureProfileForDocumentType(document.ProfileId)?.Batch.Fields ?? [];
        var results = await _exportRunner.RunAsync(
            profile, document, row.Indexes, batchFields, alreadyExported: document.CompletedExportIds).ConfigureAwait(true);
        await RecordExportsAsync(row, profile, results).ConfigureAwait(true);
        if (results.FirstOrDefault(result => !result.Success) is { } failure)
        {
            // The ones that worked are remembered, so retrying only sends the ones that failed — a
            // second try mustn't create the Therefore document again.
            document.CompletedExportIds.AddRange(results.Where(result => result.Success).Select(result => result.ExportId));
            await _store.UpdateAsync(document).ConfigureAwait(true);
            var done = profile.Exports.Where(export => document.CompletedExportIds.Contains(export.Id)).Select(export => $"\"{export.Name}\"").ToList();
            return (ExportOutcome.Failed, $"{document.OriginalFileName}: {failure.Message}"
                + (done.Count == 0 ? string.Empty : $" ({string.Join(", ", done)} already done — won't be sent again)"));
        }

        document.CompletedExportIds.Clear();
        document.ExportedUtc = DateTimeOffset.UtcNow;
        // Exported whether it stays in the inbox or goes to Trash — restored from Trash, it should still
        // say it was exported rather than come back Ready to be sent again.
        document.Status = DocumentStatus.Exported;

        if (_watchSettings.RemoveDocumentsAfterExport)
        {
            await _store.UpdateAsync(document).ConfigureAwait(true);
            await _store.SoftDeleteAsync(document.Id).ConfigureAwait(true);
            Documents.Remove(row);
            SelectedDocuments.Remove(row);
            return (ExportOutcome.ExportedAndRemoved, null);
        }

        await _store.UpdateAsync(document).ConfigureAwait(true);
        row.NotifyIndexes();
        return (ExportOutcome.Exported, null);
    }
}
