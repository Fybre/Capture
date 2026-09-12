using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
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
    private sealed record ScannedPageInfo(string ImagePath, int Width, int Height, int Dpi);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanning;

    private CancellationTokenSource? _scanCancellation;

    // Uses the preferred scanner and source selected in Settings, falling back to the first currently
    // available device if that scanner has since been disconnected. Loops: after a scan resolves to
    // exactly one document, prompts to scan another pass and append it to that same document (skipping
    // batch/document boundary detection entirely for the appended pages — see
    // IPageManagementService.AppendPagesAsync) rather than starting a fresh, independent document.
    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RunScanAppendLoopAsync(initialAppendTargetDocumentId: null);

    // Scans, moves whatever document(s) result into CurrentSharedBatchId — set by selecting a whole
    // batch (e.g. clicking its divider row; see MainWindow.axaml.cs's SelectBatchDocuments) — then, like
    // the default Scan action, offers to keep scanning more documents into that same batch. Deliberately
    // ignores the incoming scan's own batch-boundary rules: whatever batch(es) the capture profile would
    // normally have split each pass into, every resulting document ends up moved into the one target
    // batch regardless, via the same MoveDocumentToBatchAsync a manual drag-to-batch move already uses
    // (which also cleans up each freshly-created batch once it's empty). Document-boundary rules are NOT
    // bypassed — this only ever targets which batch, never how a pass gets split into documents.
    [RelayCommand(CanExecute = nameof(CanScanToCurrentBatch))]
    private Task ScanToCurrentBatchAsync() =>
        CurrentSharedBatchId is { } targetBatchId ? RunScanToBatchLoopAsync(targetBatchId) : Task.CompletedTask;

    // Scans and appends every resulting page directly onto CurrentSingleDocument, then offers to keep
    // scanning more pages onto it — no batch or document boundary detection at all, matching
    // AppendPagesAsync's own contract (see IPageManagementService.AppendPagesAsync). This is exactly
    // ScanAsync's own append behavior with the target pre-selected instead of inferred from the first
    // pass's result, so it shares the same loop.
    [RelayCommand(CanExecute = nameof(CanScanToCurrentDocument))]
    private Task ScanToCurrentDocumentAsync() =>
        CurrentSingleDocument is { } targetRow ? RunScanAppendLoopAsync(targetRow.Id) : Task.CompletedTask;

    /// <summary>Shared loop behind <see cref="ScanAsync"/> and <see cref="ScanToCurrentDocumentAsync"/>:
    /// scan a pass, either classify it normally (first pass, when no target is pre-selected) or append
    /// it onto <paramref name="initialAppendTargetDocumentId"/>/whatever the first pass resolved to, then
    /// prompt to continue scanning more pages onto that same document.</summary>
    private async Task RunScanAppendLoopAsync(Guid? initialAppendTargetDocumentId)
    {
        IsBusy = true;
        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;
        StatusIsError = false;
        var appendTargetDocumentId = initialAppendTargetDocumentId;
        try
        {
            var device = await ResolveScanDeviceAsync(cancellationToken).ConfigureAwait(true);
            if (device is null)
                return;

            while (true)
            {
                var scannedPages = await RunOneScanPassAsync(device, cancellationToken).ConfigureAwait(true);
                if (scannedPages.Count == 0)
                    break;

                try
                {
                    if (appendTargetDocumentId is { } targetId)
                    {
                        StatusText = "Appending scanned pages to the document…";
                        await AppendScannedPagesAsync(targetId, scannedPages, cancellationToken).ConfigureAwait(true);
                        StatusText = $"Appended {scannedPages.Count} page(s) to the document";
                        StatusIsError = false;
                    }
                    else
                    {
                        // A multi-page ADF/feeder scan becomes one multi-page document (or several, if
                        // the capture profile splits on separator pages) — the same way a multi-page PDF
                        // or TIFF import already does — rather than one document per physical page.
                        var documents = await ImportScannedPagesAsync(scannedPages, DocumentSource.Scan).ConfigureAwait(true);
                        // Only offer to keep appending when this scan resolved unambiguously to one
                        // document — if boundary rules split it into several (or produced none), there's
                        // no single target left to grow, so a next pass would just be independent.
                        appendTargetDocumentId = documents.Count == 1 ? documents[0].Id : null;
                    }
                }
                finally
                {
                    DeleteScannedPageFiles(scannedPages);
                }

                if (appendTargetDocumentId is null || _dialogs.Host is not { } host)
                    break;

                var continueScanning = await _confirm.ConfirmAsync(
                    host,
                    "Continue scanning?",
                    "Scan another page and append it to this document, or finish here?",
                    confirmText: "Continue scanning",
                    cancelText: "Finish").ConfigureAwait(true);
                if (!continueScanning)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            StatusIsError = true;
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            IsScanning = false;
            IsBusy = false;
        }
    }

    /// <summary>Shared loop behind <see cref="ScanToCurrentBatchAsync"/>: scan a pass, classify it
    /// normally, move every resulting document into <paramref name="targetBatchId"/>, then prompt to
    /// continue scanning more documents into that same batch.</summary>
    private async Task RunScanToBatchLoopAsync(Guid targetBatchId)
    {
        IsBusy = true;
        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;
        StatusIsError = false;
        try
        {
            var device = await ResolveScanDeviceAsync(cancellationToken).ConfigureAwait(true);
            if (device is null)
                return;

            while (true)
            {
                var scannedPages = await RunOneScanPassAsync(device, cancellationToken).ConfigureAwait(true);
                if (scannedPages.Count == 0)
                    break;

                try
                {
                    var documents = await ImportScannedPagesAsync(scannedPages, DocumentSource.Scan).ConfigureAwait(true);
                    foreach (var document in documents)
                        await MoveDocumentToBatchAsync(document.Id, targetBatchId).ConfigureAwait(true);
                    StatusText = documents.Count == 1
                        ? "Scanned 1 document into the current batch"
                        : $"Scanned {documents.Count} document(s) into the current batch";
                    StatusIsError = false;
                }
                finally
                {
                    DeleteScannedPageFiles(scannedPages);
                }

                if (_dialogs.Host is not { } host)
                    break;

                var continueScanning = await _confirm.ConfirmAsync(
                    host,
                    "Continue scanning?",
                    "Scan another document into this batch, or finish here?",
                    confirmText: "Continue scanning",
                    cancelText: "Finish").ConfigureAwait(true);
                if (!continueScanning)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            StatusIsError = true;
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            IsScanning = false;
            IsBusy = false;
        }
    }

    private async Task<ScanDevice?> ResolveScanDeviceAsync(CancellationToken cancellationToken)
    {
        var devices = await _scanSource.ListDevicesAsync(cancellationToken).ConfigureAwait(true);
        if (devices.Count > 0)
            return devices.FirstOrDefault(item => item.Id == _watchSettings.ScanPreferredDeviceId) ?? devices[0];

        StatusText = "No scanner found";
        StatusIsError = true;
        return null;
    }

    private async Task<List<ScannedPageInfo>> RunOneScanPassAsync(ScanDevice device, CancellationToken cancellationToken)
    {
        var colorMode = _watchSettings.ScanGrayscale ? ScanColorMode.Grayscale : ScanColorMode.Color;
        var sourceKind = _watchSettings.ScanSource == ScanInputSource.Feeder
            ? ScanSourceKind.Feeder
            : ScanSourceKind.Flatbed;
        var options = new ScanOptions(device.Id, _watchSettings.ScanDpi, _watchSettings.ScanDuplex, colorMode, sourceKind);

        IsScanning = true;
        StatusText = $"Scanning from {device.Name}…";
        var scannedPages = new List<ScannedPageInfo>();
        try
        {
            await foreach (var page in _scanSource.ScanAsync(options, cancellationToken).ConfigureAwait(true))
                scannedPages.Add(new ScannedPageInfo(page.FilePath, page.Width, page.Height, page.Dpi));
        }
        finally
        {
            IsScanning = false;
        }

        if (scannedPages.Count == 0)
            StatusText = "Scan produced no pages";

        return scannedPages;
    }

    private async Task<CaptureDocument> AppendScannedPagesAsync(
        Guid targetDocumentId, IReadOnlyList<ScannedPageInfo> scannedPages, CancellationToken cancellationToken)
    {
        var rasterPages = scannedPages
            .Select((page, index) => new RasterPage(index + 1, page.ImagePath, page.Width, page.Height, page.Dpi))
            .ToList();
        var updated = await _pageManagement.AppendPagesAsync(targetDocumentId, rasterPages, cancellationToken).ConfigureAwait(true);
        await ReloadDocumentsAsync().ConfigureAwait(true);
        if (IsPreviewMode)
            SelectedDocument = Documents.FirstOrDefault(row => row.Id == updated.Id);
        return updated;
    }

    private static void DeleteScannedPageFiles(IReadOnlyList<ScannedPageInfo> pages)
    {
        foreach (var page in pages)
        {
            try { File.Delete(page.ImagePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* best-effort cleanup of our own temp file */ }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan() => _scanCancellation?.Cancel();

    private bool CanCancelScan() => IsScanning && _scanCancellation is not null;

    private bool CanScan() => _scanSource.IsAvailable && !IsBusy;

    private bool CanScanToCurrentBatch() => CanScan() && CurrentSharedBatchId is not null;

    private bool CanScanToCurrentDocument() => CanScan() && CurrentSingleDocument is not null;
}
