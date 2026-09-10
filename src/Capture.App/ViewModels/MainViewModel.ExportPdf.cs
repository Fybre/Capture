using Capture.App.Services;
using Capture.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class MainViewModel
{
    // Acts strictly on the current selection — unlike ExportAsync, exporting the entire inbox as one
    // ad hoc PDF isn't a sensible default, so this deliberately does not fall back to "all" when
    // nothing is selected.
    [RelayCommand(CanExecute = nameof(CanExportSelectedToPdf))]
    private async Task ExportSelectedToPdfAsync()
    {
        var rows = SelectedDocuments
            .OrderBy(row => Documents.IndexOf(row))
            .ToList();
        if (rows.Count == 0 || _dialogs.Host is not { } host)
            return;

        var suggestedFileName = rows.Count == 1
            ? UniqueFileName([], rows[0].Document.OriginalFileName)
            : "Export.pdf";
        var options = await _exportPdfDialog.ShowAsync(host, rows.Count, suggestedFileName).ConfigureAwait(true);
        if (options is null)
            return;

        IsBusy = true;
        try
        {
            if (options.Combine)
            {
                var pages = new List<DocumentPage>();
                foreach (var row in rows)
                    pages.AddRange(await _store.GetPagesAsync(row.Document.Id).ConfigureAwait(true));
                await _pdfExportWriter.WriteAsync(pages, options.DestinationPath, options.Compress).ConfigureAwait(true);
                StatusText = $"Exported {rows.Count} document(s) to {Path.GetFileName(options.DestinationPath)}";
            }
            else
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    var pages = await _store.GetPagesAsync(row.Document.Id).ConfigureAwait(true);
                    var fileName = UniqueFileName(usedNames, row.Document.OriginalFileName);
                    var outputPath = Path.Combine(options.DestinationPath, fileName);
                    await _pdfExportWriter.WriteAsync(pages, outputPath, options.Compress).ConfigureAwait(true);
                }
                StatusText = $"Exported {rows.Count} document(s) to {options.DestinationPath}";
            }

            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Export to PDF failed: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExportSelectedToPdf() => !IsBusy && !ShowTrash && SelectedDocuments.Count > 0;

    private static string UniqueFileName(HashSet<string> usedNames, string originalFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Document";

        var candidate = $"{baseName}.pdf";
        var suffix = 2;
        while (!usedNames.Add(candidate))
            candidate = $"{baseName} ({suffix++}).pdf";
        return candidate;
    }
}
