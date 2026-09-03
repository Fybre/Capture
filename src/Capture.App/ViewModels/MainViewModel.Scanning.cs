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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanning;

    private CancellationTokenSource? _scanCancellation;

    // Uses the preferred scanner and source selected in Settings, falling back to the first currently
    // available device if that scanner has since been disconnected.
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsScanning = true;
        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;
        var scannedPages = new List<ScannedPageInfo>();
        try
        {
            var devices = await _scanSource.ListDevicesAsync(cancellationToken).ConfigureAwait(true);
            if (devices.Count == 0)
            {
                StatusText = "No scanner found";
                return;
            }

            var device = devices.FirstOrDefault(item => item.Id == _watchSettings.ScanPreferredDeviceId) ?? devices[0];
            var colorMode = _watchSettings.ScanGrayscale ? ScanColorMode.Grayscale : ScanColorMode.Color;
            var source = _watchSettings.ScanSource == ScanInputSource.Feeder
                ? ScanSourceKind.Feeder
                : ScanSourceKind.Flatbed;
            StatusText = $"Scanning from {device.Name}…";
            var options = new ScanOptions(device.Id, _watchSettings.ScanDpi, _watchSettings.ScanDuplex, colorMode, source);
            await foreach (var page in _scanSource.ScanAsync(options, cancellationToken).ConfigureAwait(true))
                scannedPages.Add(new ScannedPageInfo(page.FilePath, page.Width, page.Height, page.Dpi));

            IsScanning = false;

            if (scannedPages.Count == 0)
            {
                StatusText = "Scan produced no pages";
                return;
            }

            // A multi-page ADF/feeder scan becomes one multi-page document (or several, if the
            // profile/batch profile splits on separator pages) — the same way a multi-page PDF or
            // TIFF import already does — rather than one document per physical page.
            await ImportScannedPagesAsync(scannedPages, DocumentSource.Scan).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            foreach (var page in scannedPages)
            {
                try { File.Delete(page.ImagePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { /* best-effort cleanup of our own temp file */ }
            }
            _scanCancellation.Dispose();
            _scanCancellation = null;
            IsScanning = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan() => _scanCancellation?.Cancel();

    private bool CanCancelScan() => IsScanning && _scanCancellation is not null;

    private bool CanScan() => _scanSource.IsAvailable && !IsBusy;
}
