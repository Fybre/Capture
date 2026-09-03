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
    private enum ExportOutcome { Exported, ExportedAndRemoved, Skipped, Failed }

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
            var skipped = 0;
            foreach (var row in rows)
            {
                switch (await ExportDocumentAsync(row).ConfigureAwait(true))
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
                    case ExportOutcome.Skipped:
                        skipped++;
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
                parts.Add($"{failed} failed");
            if (skipped > 0)
                parts.Add($"{skipped} skipped (not ready, or no export configured)");
            StatusText = parts.Count == 0 ? "Nothing to export" : string.Join(", ", parts);
            if (exported > 0 || removed > 0 || failed > 0)
            {
                if (failed > 0) _toasts.ShowError(StatusText); else _toasts.ShowSuccess(StatusText);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Only a Ready document is eligible — one that's still NeedsReview, Queued, Processing, or Error
    // is skipped rather than exported with incomplete/unvalidated data. An already-Exported document is
    // also skipped: re-running export is available per-document by selecting it and using Export, not
    // implicitly folded into a bulk export-all pass.
    private async Task<ExportOutcome> ExportDocumentAsync(DocumentRow row)
    {
        var document = row.Document;
        if (document.Status != DocumentStatus.Ready)
            return ExportOutcome.Skipped;

        var profile = document.ProfileId is { } profileId ? Profiles.FirstOrDefault(item => item.Id == profileId) : null;
        if (profile is null || profile.Exports.Count(item => item.Enabled) == 0)
            return ExportOutcome.Skipped;

        var results = await _exportRunner.RunAsync(profile, document, row.Indexes).ConfigureAwait(true);
        if (results.Any(result => !result.Success))
            return ExportOutcome.Failed;

        var importProfile = document.ImportProfileId is { } importProfileId
            ? ImportProfiles.FirstOrDefault(item => item.Id == importProfileId)
            : null;
        if (importProfile?.RemoveAfterExport == true)
        {
            await _store.SoftDeleteAsync(document.Id).ConfigureAwait(true);
            Documents.Remove(row);
            SelectedDocuments.Remove(row);
            return ExportOutcome.ExportedAndRemoved;
        }

        document.Status = DocumentStatus.Exported;
        await _store.UpdateAsync(document).ConfigureAwait(true);
        row.NotifyIndexes();
        return ExportOutcome.Exported;
    }
}
