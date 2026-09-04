using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Media.Imaging;
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

public partial class ImportProfileDesignerViewModel : ViewModelBase
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IImportProfileStore _store;
    private readonly IProfileStore _profileStore;
    private readonly IBatchProfileStore _batchProfileStore;
    private readonly IProfileDialogService _profileDialog;
    private readonly IBatchProfileDialogService _batchProfileDialog;
    private readonly IFileDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly IToastService _toasts;
    private readonly IBarcodeDecoder? _barcodes;
    private readonly ILatticeBuilder? _latticeBuilder;
    private readonly List<string> _pageImagePaths = [];
    private readonly Dictionary<int, PageLattice> _lattices = [];
    private int _loadGeneration;

    public ImportProfileDesignerViewModel(
        ImportProfile profile,
        bool isNew,
        IImportProfileStore store,
        IProfileStore profileStore,
        IBatchProfileStore batchProfileStore,
        IProfileDialogService profileDialog,
        IBatchProfileDialogService batchProfileDialog,
        IFileDialogService dialogs,
        IAppPaths paths,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        IToastService toasts,
        IBarcodeDecoder? barcodes = null,
        ILatticeBuilder? latticeBuilder = null)
    {
        Profile = profile;
        IsNew = isNew;
        _store = store;
        _profileStore = profileStore;
        _batchProfileStore = batchProfileStore;
        _profileDialog = profileDialog;
        _batchProfileDialog = batchProfileDialog;
        _dialogs = dialogs;
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _toasts = toasts;
        _barcodes = barcodes;
        _latticeBuilder = latticeBuilder;

        _name = profile.Name;
        _sampleFileName = profile.SampleFileName;
        _matchMode = profile.MatchMode;
        _matchMinimum = Math.Max(1, profile.MatchMinimum);

        foreach (var strategy in profile.Strategies)
            Strategies.Add(new SeparationStrategyRow(strategy));
    }

    public ImportProfile Profile { get; }

    public bool IsNew { get; }

    public bool Saved { get; private set; }

    public ICommand? CloseCommand { get; set; }

    public IReadOnlyList<SeparationMatchMode> MatchModeOptions { get; } = Enum.GetValues<SeparationMatchMode>();

    public IReadOnlyList<SeparationStrategyType> StrategyTypeOptions { get; } = Enum.GetValues<SeparationStrategyType>();

    public IReadOnlyList<string> BarcodeFormatOptions => BarcodePatterns.KnownFormats;

    public ObservableCollection<SeparationStrategyRow> Strategies { get; } = [];

    public ObservableCollection<IndexingProfileOption> IndexingProfileOptions { get; } = [];

    public ObservableCollection<BatchProfile> BatchProfileOptions { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtLeast))]
    private SeparationMatchMode _matchMode;

    [ObservableProperty]
    private int _matchMinimum = 1;

    public bool IsAtLeast => MatchMode == SeparationMatchMode.AtLeast;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeSampleCommand))]
    private string? _sampleFileName;

    [ObservableProperty]
    private BatchProfile? _selectedBatchProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestMatchCommand))]
    private SeparationStrategyRow? _selectedStrategy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    partial void OnSelectedStrategyChanged(SeparationStrategyRow? value)
    {
        if (value?.Zone is { } zone && zone.PageNumber != CurrentPageNumber
            && zone.PageNumber >= 1 && zone.PageNumber <= SamplePageCount)
        {
            CurrentPageNumber = zone.PageNumber;
            _ = ShowPageAsync();
        }
        else
        {
            RefreshHighlights();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestMatchCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _currentPageNumber = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _samplePageCount;

    public string PageLabel => SamplePageCount == 0 ? "—" : $"{CurrentPageNumber} / {SamplePageCount}";

    [ObservableProperty]
    private IReadOnlyList<IndexHighlight> _highlights = [];

    public async Task InitializeAsync()
    {
        await ReloadIndexingProfileOptionsAsync().ConfigureAwait(true);
        await ReloadBatchProfileOptionsAsync().ConfigureAwait(true);

        _pageImagePaths.Clear();
        _pageImagePaths.AddRange(GetPageImagePaths());
        SamplePageCount = _pageImagePaths.Count;
        CurrentPageNumber = SamplePageCount == 0 ? 1 : Math.Clamp(CurrentPageNumber, 1, SamplePageCount);
        await ShowPageAsync().ConfigureAwait(true);
        StatusText = SamplePageCount == 0 ? "No sample pages" : string.Empty;

        SelectedStrategy = Strategies.FirstOrDefault();
    }

    // Preserves existing checkbox selections across a reload — used both by InitializeAsync and after
    // returning from the "New indexing profile…" sub-dialog, so a profile created there shows up
    // immediately without losing anything already checked.
    private async Task ReloadIndexingProfileOptionsAsync()
    {
        var selectedIds = IndexingProfileOptions.Where(option => option.IsSelected).Select(option => option.Id).ToHashSet();
        IndexingProfileOptions.Clear();
        foreach (var indexingProfile in await _profileStore.GetAllAsync().ConfigureAwait(true))
        {
            IndexingProfileOptions.Add(new IndexingProfileOption(indexingProfile)
            {
                IsSelected = Profile.IndexingProfileIds.Contains(indexingProfile.Id) || selectedIds.Contains(indexingProfile.Id)
            });
        }
    }

    // Same idea as ReloadIndexingProfileOptionsAsync — preserves the current selection (by Id) across
    // a reload, used both by InitializeAsync and after returning from "New batch profile…".
    private async Task ReloadBatchProfileOptionsAsync()
    {
        var selectedId = SelectedBatchProfile?.Id ?? Profile.BatchProfileId;
        BatchProfileOptions.Clear();
        foreach (var batchProfile in await _batchProfileStore.GetAllAsync().ConfigureAwait(true))
            BatchProfileOptions.Add(batchProfile);

        SelectedBatchProfile = selectedId is { } id
            ? BatchProfileOptions.FirstOrDefault(p => p.Id == id)
            : null;
    }

    [RelayCommand]
    private async Task ManageIndexingProfilesAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        await _profileDialog.ShowAsync(host).ConfigureAwait(true);
        await ReloadIndexingProfileOptionsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ManageBatchProfilesAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        await _batchProfileDialog.ShowAsync(host).ConfigureAwait(true);
        await ReloadBatchProfileOptionsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void AddStrategy()
    {
        // EveryNPages is the friendliest default to add — it needs no sample/zone at all, so a newly
        // added strategy is immediately usable rather than sitting there needing setup before anything
        // else can happen.
        var row = new SeparationStrategyRow(new SeparationStrategy { Id = Guid.NewGuid(), Type = SeparationStrategyType.EveryNPages });
        Strategies.Add(row);
        SelectedStrategy = row;
    }

    [RelayCommand]
    private void RemoveStrategy(SeparationStrategyRow? row)
    {
        if (row is null)
            return;

        Strategies.Remove(row);
        if (SelectedStrategy == row)
            SelectedStrategy = null;
        RefreshHighlights();
    }

    private IReadOnlyList<string> GetPageImagePaths()
    {
        var directory = _paths.ImportProfilePagesDirectory(Profile.Id);
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.png")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [RelayCommand(CanExecute = nameof(CanChangeSample))]
    private async Task ChangeSampleAsync()
    {
        var path = await _dialogs.PickFileAsync("Choose a sample document").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsBusy = true;
        try
        {
            StatusText = "Preparing sample…";
            await PrepareSampleAsync(path).ConfigureAwait(true);
            SampleFileName = Profile.SampleFileName;
            _pageImagePaths.Clear();
            _pageImagePaths.AddRange(GetPageImagePaths());
            _lattices.Clear();
            SamplePageCount = _pageImagePaths.Count;
            CurrentPageNumber = 1;
            await ShowPageAsync().ConfigureAwait(true);
            StatusText = SamplePageCount == 0 ? "No sample pages" : "Sample updated";
            if (SamplePageCount == 0) _toasts.ShowError(StatusText); else _toasts.ShowSuccess(StatusText);
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

    private bool CanChangeSample() => !IsBusy;

    // Deliberately not reusing IProfileSampleService — it's typed to IndexingProfile specifically (its
    // PrepareAsync signature, and the ProfileDirectory/ProfilePagesDirectory paths it writes to, both
    // assume an IndexingProfile). ImportProfile only ever needs the rasterized page images; any OCR
    // lattice a strategy card needs is built lazily and separately, see EnsureLatticeAsync.
    private async Task PrepareSampleAsync(string sourcePath)
    {
        _paths.EnsureCreated();
        var pagesDirectory = _paths.ImportProfilePagesDirectory(Profile.Id);

        if (Directory.Exists(pagesDirectory))
            Directory.Delete(pagesDirectory, recursive: true);
        var profileDirectory = _paths.ImportProfileDirectory(Profile.Id);
        if (Directory.Exists(profileDirectory))
        {
            foreach (var stale in Directory.EnumerateFiles(profileDirectory, "sample.*"))
                File.Delete(stale);
        }

        Directory.CreateDirectory(pagesDirectory);

        var originalName = Path.GetFileName(sourcePath);
        var samplePath = _paths.ImportProfileSamplePath(Profile.Id, originalName);
        File.Copy(sourcePath, samplePath, overwrite: true);
        Profile.SampleFileName = originalName;

        _ = ImportFormats.IsPdf(sourcePath)
            ? await _pdfRasterizer.RasterizeAsync(samplePath, pagesDirectory, DocumentImporter.PreviewDpi, CancellationToken.None).ConfigureAwait(false)
            : await _imageImporter.ImportAsync(samplePath, pagesDirectory, CancellationToken.None).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage()
    {
        CurrentPageNumber--;
        _ = ShowPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        CurrentPageNumber++;
        _ = ShowPageAsync();
    }

    private bool CanGoPrevious() => !IsBusy && CurrentPageNumber > 1;

    private bool CanGoNext() => !IsBusy && CurrentPageNumber < SamplePageCount;

    private async Task ShowPageAsync()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        SetPageImage(null);

        if (CurrentPageNumber < 1 || CurrentPageNumber > _pageImagePaths.Count)
        {
            Highlights = [];
            return;
        }

        var path = _pageImagePaths[CurrentPageNumber - 1];
        var bitmap = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }).ConfigureAwait(true);

        if (generation != _loadGeneration)
        {
            bitmap.Dispose();
            return;
        }

        SetPageImage(bitmap);
        RefreshHighlights();
    }

    private void SetPageImage(Bitmap? bitmap)
    {
        var previous = PageImage;
        PageImage = bitmap;
        previous?.Dispose();
    }

    private void RefreshHighlights()
    {
        Highlights = Strategies
            .Where(row => row.NeedsZone && row.Zone is { } zone && zone.PageNumber == CurrentPageNumber)
            .Select(row => new IndexHighlight
            {
                FieldId = row.Id,
                FieldName = row.DisplayLabel,
                X = row.Zone!.X,
                Y = row.Zone.Y,
                Width = row.Zone.Width,
                Height = row.Zone.Height,
                IsSelected = SelectedStrategy?.Id == row.Id
            })
            .ToList();
    }

    [RelayCommand]
    private void CompleteZone(NormalizedRect rect)
    {
        if (SelectedStrategy is not { NeedsZone: true } row || rect.Width < 0.004f || rect.Height < 0.004f)
            return;

        row.Zone = new ZoneRect
        {
            PageNumber = CurrentPageNumber,
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height
        };
        row.ZonePageNumber = CurrentPageNumber;
        RefreshHighlights();
        _ = DetectAsync(row);
    }

    [RelayCommand]
    private void ChangeZone(NormalizedRect rect)
    {
        if (SelectedStrategy is not { NeedsZone: true } row || row.Zone is null)
            return;

        var zone = row.Zone;
        zone.X = Math.Clamp(rect.X, 0, 1);
        zone.Y = Math.Clamp(rect.Y, 0, 1);
        zone.Width = Math.Clamp(rect.Width, 0.002f, 1);
        zone.Height = Math.Clamp(rect.Height, 0.002f, 1);
        zone.PageNumber = CurrentPageNumber;
        row.ZonePageNumber = CurrentPageNumber;
        RefreshHighlights();
        _ = DetectAsync(row);
    }

    // Attempts to detect a real value from the zone just drawn/adjusted and pre-fills the row's
    // config from what's actually there, since typing a symbology/regex by hand is unnecessary
    // busywork when a real sample is right in front of the user. Fields stay freely editable/
    // clearable afterward — this only ever sets a starting point.
    private async Task DetectAsync(SeparationStrategyRow row)
    {
        if (row.Zone is null)
            return;

        if (row.IsBarcode)
        {
            DetectBarcode(row);
            return;
        }

        if (row.IsOcrZone)
        {
            await DetectOcrZoneAsync(row).ConfigureAwait(true);
            return;
        }

        if (row.IsSimilarity)
            StatusText = "Reference zone set — embedding comparison isn't available yet";
    }

    private void DetectBarcode(SeparationStrategyRow row)
    {
        if (_barcodes is null || row.Zone is null)
            return;

        var pageIndex = CurrentPageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageImagePaths.Count)
            return;

        var decoded = _barcodes.Decode(_pageImagePaths[pageIndex], row.Zone);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
        {
            StatusText = "Barcode zone set — no barcode detected there; enter the type/value manually if needed";
            return;
        }

        row.BarcodeFormat = decoded.Format;
        row.BarcodeValuePattern = $"^{Regex.Escape(decoded.Text)}$";
        StatusText = $"Detected {BarcodePatterns.DisplayType(decoded.Format)}: {decoded.Text}";
    }

    private async Task DetectOcrZoneAsync(SeparationStrategyRow row)
    {
        if (row.Zone is null)
            return;

        var lattice = await EnsureLatticeAsync(CurrentPageNumber).ConfigureAwait(true);
        if (lattice is null)
        {
            StatusText = "No text found on this page to detect from";
            return;
        }

        var extracted = ZonalExtractor.Extract(lattice, row.Zone);
        if (string.IsNullOrWhiteSpace(extracted.Text))
        {
            StatusText = "OCR zone set — no text detected there; enter the pattern manually if needed";
            return;
        }

        var text = extracted.Text.Trim();
        row.TextPattern = $"^{Regex.Escape(text)}$";
        StatusText = $"Detected text: {text}";
    }

    // Builds (and caches, per sample page) the OCR lattice a Regex/OcrZone strategy card needs to
    // preview/test against — built lazily, only when a card actually asks for it, mirroring
    // ProfileDesignerViewModel's own per-page lattice cache. The throwaway CaptureDocument/DocumentPage
    // pair is never persisted, same pattern DocumentImporter uses to build page text/lattices before
    // any real document exists.
    private async Task<PageLattice?> EnsureLatticeAsync(int pageNumber)
    {
        if (_lattices.TryGetValue(pageNumber, out var cached))
            return cached;

        if (_latticeBuilder is null)
            return null;

        var pageIndex = pageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageImagePaths.Count)
            return null;

        var imagePath = _pageImagePaths[pageIndex];
        var throwawayId = Guid.NewGuid();
        var throwawayDocument = new CaptureDocument { OriginalFileName = string.Empty, StoredPath = imagePath };
        var throwawayPage = new DocumentPage
        {
            DocumentId = throwawayId,
            PageNumber = pageNumber,
            SourcePageNumber = pageNumber,
            ImagePath = imagePath
        };

        var lattice = await _latticeBuilder.BuildPageAsync(throwawayDocument, throwawayPage, CancellationToken.None).ConfigureAwait(false);
        _lattices[pageNumber] = lattice;
        return lattice;
    }

    // Re-evaluates the selected strategy against the current sample and reports whether it would
    // actually match — unlike DetectAsync (which overwrites the row's fields), this leaves them
    // untouched. Useful after hand-editing a pattern (e.g. loosening it to a prefix).
    [RelayCommand(CanExecute = nameof(CanTestMatch))]
    private async Task TestMatchAsync()
    {
        if (SelectedStrategy is not { } row)
            return;

        if (row.IsBarcode)
        {
            TestBarcodeMatch(row);
            return;
        }

        if (row.IsOcrZone)
        {
            await TestOcrZoneMatchAsync(row).ConfigureAwait(true);
            return;
        }

        if (row.IsRegex)
        {
            await TestRegexMatchAsync(row).ConfigureAwait(true);
            return;
        }

        if (row.IsSimilarity)
            StatusText = "Testing isn't available yet — needs an embedding model";
    }

    private bool CanTestMatch() => !IsBusy && SelectedStrategy is not null;

    private void TestBarcodeMatch(SeparationStrategyRow row)
    {
        if (_barcodes is null || row.Zone is null)
        {
            StatusText = "Draw a barcode zone first, then test";
            return;
        }

        var pageIndex = CurrentPageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageImagePaths.Count)
            return;

        var decoded = _barcodes.Decode(_pageImagePaths[pageIndex], row.Zone);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
        {
            StatusText = "No barcode detected in the current zone";
            return;
        }

        var formatMatches = string.IsNullOrWhiteSpace(row.BarcodeFormat)
            || string.Equals(row.BarcodeFormat, decoded.Format, StringComparison.OrdinalIgnoreCase);
        var valueMatches = BarcodePatterns.Matches(row.BarcodeValuePattern, decoded.Text);

        StatusText = formatMatches && valueMatches
            ? $"Match: {BarcodePatterns.DisplayType(decoded.Format)} “{decoded.Text}”"
            : $"No match — detected {BarcodePatterns.DisplayType(decoded.Format)} “{decoded.Text}”, which doesn't satisfy the current type/value filter";
    }

    // Mirrors PageSeparator's OcrZone evaluator exactly: an empty pattern matches any non-empty zone
    // text, same as an empty BarcodeValuePattern matches any barcode value.
    private async Task TestOcrZoneMatchAsync(SeparationStrategyRow row)
    {
        if (row.Zone is null)
        {
            StatusText = "Draw a zone first, then test";
            return;
        }

        var lattice = await EnsureLatticeAsync(CurrentPageNumber).ConfigureAwait(true);
        if (lattice is null)
        {
            StatusText = "No text found on this page to test against";
            return;
        }

        var extracted = ZonalExtractor.Extract(lattice, row.Zone);
        if (string.IsNullOrWhiteSpace(extracted.Text))
        {
            StatusText = "No text detected in the current zone";
            return;
        }

        var text = extracted.Text.Trim();
        if (string.IsNullOrWhiteSpace(row.TextPattern))
        {
            StatusText = $"Match (any non-empty text): “{text}”";
            return;
        }

        StatusText = TryRegexMatch(row.TextPattern, extracted.Text)
            ? $"Match: “{text}”"
            : $"No match — detected “{text}”, which doesn't satisfy the current pattern";
    }

    // Mirrors PageSeparator's Regex evaluator exactly: unlike OcrZone, an empty pattern never hits —
    // there's no other signal to fall back on for a whole-page match.
    private async Task TestRegexMatchAsync(SeparationStrategyRow row)
    {
        if (string.IsNullOrWhiteSpace(row.TextPattern))
        {
            StatusText = "Enter a pattern first, then test";
            return;
        }

        var lattice = await EnsureLatticeAsync(CurrentPageNumber).ConfigureAwait(true);
        if (lattice is null)
        {
            StatusText = "No text found on this page to test against";
            return;
        }

        var text = LatticeText.Build(lattice.Words).Text;
        StatusText = TryRegexMatch(row.TextPattern, text) ? "Match found on this page" : "No match on this page";
    }

    private static bool TryRegexMatch(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexMatchTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusText = "Give this import profile a name";
            _toasts.ShowError(StatusText);
            return;
        }

        Profile.Name = Name.Trim();
        Profile.MatchMode = MatchMode;
        Profile.MatchMinimum = Math.Max(1, MatchMinimum);
        Profile.Strategies = Strategies.Select(row => row.ToModel()).ToList();
        Profile.BatchProfileId = SelectedBatchProfile?.Id;
        Profile.IndexingProfileIds = IndexingProfileOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Id)
            .ToList();

        await _store.SaveAsync(Profile).ConfigureAwait(true);
        Saved = true;
        _toasts.ShowSuccess($"Saved \"{Profile.Name}\"");
        CloseCommand?.Execute(null);
    }

    public void Dispose() => SetPageImage(null);
}

public sealed partial class IndexingProfileOption : ObservableObject
{
    public IndexingProfileOption(IndexingProfile profile)
    {
        Id = profile.Id;
        Name = profile.Name;
    }

    public Guid Id { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Wraps one <see cref="SeparationStrategy"/> for editing in the Designer — like <c>FieldRow</c>
/// wraps an <c>IndexField</c>, this mirrors the model's flat, kind-specific fields as observable
/// properties so the UI can bind/edit them directly, then flattens back via <see cref="ToModel"/>.</summary>
public sealed partial class SeparationStrategyRow : ObservableObject
{
    public SeparationStrategyRow(SeparationStrategy strategy)
    {
        Id = strategy.Id;
        _type = strategy.Type;
        _name = strategy.Name;
        _pageCount = Math.Max(1, strategy.PageCount);
        _blankInkPercent = strategy.BlankInkPercent;
        _zone = strategy.Zone;
        _zonePageNumber = Math.Max(1, strategy.ZonePageNumber);
        _barcodeFormat = strategy.BarcodeFormat;
        _barcodeValuePattern = strategy.BarcodeValuePattern;
        _textPattern = strategy.TextPattern;
        _discardSeparatorPage = strategy.DiscardSeparatorPage;
        ReferenceEmbedding = strategy.ReferenceEmbedding;
        _similarityThreshold = strategy.SimilarityThreshold;
    }

    public Guid Id { get; }

    /// <summary>Not editable directly today (nothing computes one yet) — just round-trips whatever
    /// Phase C eventually sets.</summary>
    public float[]? ReferenceEmbedding { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBarcode))]
    [NotifyPropertyChangedFor(nameof(IsBlankPage))]
    [NotifyPropertyChangedFor(nameof(IsEveryNPages))]
    [NotifyPropertyChangedFor(nameof(IsRegex))]
    [NotifyPropertyChangedFor(nameof(IsOcrZone))]
    [NotifyPropertyChangedFor(nameof(IsSimilarity))]
    [NotifyPropertyChangedFor(nameof(NeedsZone))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private SeparationStrategyType _type;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string? _name;

    [ObservableProperty]
    private int _pageCount = 1;

    [ObservableProperty]
    private int _blankInkPercent;

    [ObservableProperty]
    private ZoneRect? _zone;

    [ObservableProperty]
    private int _zonePageNumber = 1;

    [ObservableProperty]
    private string? _barcodeFormat;

    [ObservableProperty]
    private string? _barcodeValuePattern;

    [ObservableProperty]
    private string? _textPattern;

    [ObservableProperty]
    private double _similarityThreshold = 0.85;

    [ObservableProperty]
    private bool _discardSeparatorPage;

    public bool IsBarcode => Type == SeparationStrategyType.Barcode;
    public bool IsBlankPage => Type == SeparationStrategyType.BlankPage;
    public bool IsEveryNPages => Type == SeparationStrategyType.EveryNPages;
    public bool IsRegex => Type == SeparationStrategyType.Regex;
    public bool IsOcrZone => Type == SeparationStrategyType.OcrZone;
    public bool IsSimilarity => Type == SeparationStrategyType.Similarity;

    /// <summary>Barcode/OcrZone need a real drawn zone; Similarity's reference region does too, once
    /// the embedding backend can use it (Phase C) — modeled now so the card's shape doesn't change
    /// later.</summary>
    public bool NeedsZone => IsBarcode || IsOcrZone || IsSimilarity;

    public string DisplayLabel => string.IsNullOrWhiteSpace(Name) ? Type.ToString() : Name!;

    public SeparationStrategy ToModel() => new()
    {
        Id = Id,
        Type = Type,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name,
        PageCount = Math.Max(1, PageCount),
        BlankInkPercent = BlankInkPercent,
        Zone = Zone,
        ZonePageNumber = ZonePageNumber,
        BarcodeFormat = string.IsNullOrWhiteSpace(BarcodeFormat) ? null : BarcodeFormat,
        BarcodeValuePattern = string.IsNullOrWhiteSpace(BarcodeValuePattern) ? null : BarcodeValuePattern,
        TextPattern = string.IsNullOrWhiteSpace(TextPattern) ? null : TextPattern,
        ReferenceEmbedding = ReferenceEmbedding,
        SimilarityThreshold = SimilarityThreshold,
        DiscardSeparatorPage = DiscardSeparatorPage
    };
}
