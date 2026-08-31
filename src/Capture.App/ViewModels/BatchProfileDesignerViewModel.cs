using System.Collections.ObjectModel;
using System.Windows.Input;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class BatchProfileDesignerViewModel : ViewModelBase
{
    private readonly IBatchProfileStore _store;
    private readonly IFileDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly ILatticeBuilder _latticeBuilder;
    private readonly IToastService _toasts;
    private readonly IBarcodeDecoder? _barcodes;
    private string? _testTempDir;

    public BatchProfileDesignerViewModel(
        BatchProfile profile,
        bool isNew,
        IBatchProfileStore store,
        IFileDialogService dialogs,
        IAppPaths paths,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        ILatticeBuilder latticeBuilder,
        IToastService toasts,
        IBarcodeDecoder? barcodes)
    {
        Profile = profile;
        IsNew = isNew;
        _store = store;
        _dialogs = dialogs;
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _latticeBuilder = latticeBuilder;
        _toasts = toasts;
        _barcodes = barcodes;

        _name = profile.Name;
        _trigger = profile.Trigger;
        _pageCount = Math.Max(1, profile.PageCount);
        _barcodeFormat = profile.BarcodeFormat;
        _barcodeValuePattern = profile.BarcodeValuePattern;
        _textPattern = profile.TextPattern;
        _discardSeparatorPage = profile.DiscardSeparatorPage;
    }

    public BatchProfile Profile { get; }

    public bool IsNew { get; }

    public bool Saved { get; private set; }

    public ICommand? CloseCommand { get; set; }

    public IReadOnlyList<BatchTrigger> TriggerOptions { get; } = Enum.GetValues<BatchTrigger>();

    public IReadOnlyList<string> BarcodeFormatOptions => BarcodePatterns.KnownFormats;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEveryNPages))]
    [NotifyPropertyChangedFor(nameof(IsBarcode))]
    [NotifyPropertyChangedFor(nameof(IsRegexMatch))]
    [NotifyPropertyChangedFor(nameof(NeedsPageScan))]
    private BatchTrigger _trigger;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private string? _barcodeFormat;

    [ObservableProperty]
    private string? _barcodeValuePattern;

    [ObservableProperty]
    private string? _textPattern;

    [ObservableProperty]
    private bool _discardSeparatorPage;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    [ObservableProperty]
    private bool _isBusy;

    public bool IsEveryNPages => Trigger == BatchTrigger.EveryNPages;

    public bool IsBarcode => Trigger == BatchTrigger.Barcode;

    public bool IsRegexMatch => Trigger == BatchTrigger.RegexMatch;

    public bool NeedsPageScan => IsBarcode || IsRegexMatch;

    [ObservableProperty]
    private string? _testFileName;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testSummary = string.Empty;

    public ObservableCollection<string> TestHits { get; } = [];

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusText = "Give this batch profile a name";
            _toasts.ShowError(StatusText);
            return;
        }

        Profile.Name = Name.Trim();
        Profile.Trigger = Trigger;
        Profile.PageCount = Math.Max(1, PageCount);
        Profile.BarcodeFormat = string.IsNullOrWhiteSpace(BarcodeFormat) ? null : BarcodeFormat;
        Profile.BarcodeValuePattern = string.IsNullOrWhiteSpace(BarcodeValuePattern) ? null : BarcodeValuePattern;
        Profile.TextPattern = string.IsNullOrWhiteSpace(TextPattern) ? null : TextPattern;
        Profile.DiscardSeparatorPage = DiscardSeparatorPage;

        await _store.SaveAsync(Profile).ConfigureAwait(true);
        Saved = true;
        _toasts.ShowSuccess($"Saved \"{Profile.Name}\"");
        CloseCommand?.Execute(null);
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var path = await _dialogs.PickFileAsync("Choose a document to test batch separation against").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsBusy = true;
        IsTesting = true;
        TestHits.Clear();
        TestSummary = string.Empty;
        TestFileName = Path.GetFileName(path);
        try
        {
            CleanupTestArtifacts();
            _testTempDir = Path.Combine(_paths.WorkDirectory, "batch-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testTempDir);

            if (!ImportFormats.IsSupported(path))
                throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(path)}");

            var rasters = ImportFormats.IsPdf(path)
                ? await _pdfRasterizer.RasterizeAsync(path, _testTempDir, DocumentImporter.PreviewDpi, CancellationToken.None).ConfigureAwait(true)
                : await _imageImporter.ImportAsync(path, _testTempDir, CancellationToken.None).ConfigureAwait(true);

            var testProfile = new BatchProfile
            {
                Trigger = Trigger,
                PageCount = Math.Max(1, PageCount),
                BarcodeFormat = string.IsNullOrWhiteSpace(BarcodeFormat) ? null : BarcodeFormat,
                BarcodeValuePattern = string.IsNullOrWhiteSpace(BarcodeValuePattern) ? null : BarcodeValuePattern,
                TextPattern = string.IsNullOrWhiteSpace(TextPattern) ? null : TextPattern,
                DiscardSeparatorPage = DiscardSeparatorPage
            };

            var hits = await BatchSeparator.DetectAsync(
                    rasters, testProfile, _barcodes, CreatePageTextProvider(path), CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var hit in hits.OrderBy(item => item.PageNumber))
                TestHits.Add($"Page {hit.PageNumber} — captured \"{hit.CapturedValue}\"" + (hit.DiscardPage ? " (page discarded)" : string.Empty));

            TestSummary = Summarize(rasters.Count, hits.Select(item => item.PageNumber).ToHashSet());
        }
        catch (Exception ex)
        {
            TestSummary = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Summarize(int pageCount, HashSet<int> boundaryPages)
    {
        if (pageCount == 0)
            return "No pages were produced from that file.";

        var batches = new List<(int Start, int End)>();
        var start = 1;
        for (var page = 2; page <= pageCount; page++)
        {
            if (boundaryPages.Contains(page))
            {
                batches.Add((start, page - 1));
                start = page;
            }
        }

        batches.Add((start, pageCount));

        var ranges = string.Join(", ", batches.Select(batch =>
            batch.Start == batch.End ? $"{batch.Start}" : $"{batch.Start}–{batch.End}"));
        return $"{pageCount} page(s) → {batches.Count} batch(es): pages {ranges}";
    }

    private Func<RasterPage, CancellationToken, Task<string>> CreatePageTextProvider(string sourcePath) =>
        async (raster, ct) =>
        {
            var throwawayDocument = new CaptureDocument { OriginalFileName = string.Empty, StoredPath = sourcePath };
            var throwawayPage = new DocumentPage
            {
                DocumentId = Guid.NewGuid(),
                PageNumber = raster.PageNumber,
                SourcePageNumber = raster.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            };
            var lattice = await _latticeBuilder.BuildPageAsync(throwawayDocument, throwawayPage, ct).ConfigureAwait(false);
            return LatticeText.Build(lattice.Words).Text;
        };

    public void CleanupTestArtifacts()
    {
        if (_testTempDir is null)
            return;

        try
        {
            Directory.Delete(_testTempDir, recursive: true);
        }
        catch
        {
        }

        _testTempDir = null;
    }
}
