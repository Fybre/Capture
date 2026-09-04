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

/// <summary>
/// Mirrors <see cref="ImportProfileDesignerViewModel"/> closely — same buildable strategy list, sample
/// document + zone drawing, Test/Discard cards — restricted to the strategy types that make sense for a
/// batch boundary (<see cref="SeparationStrategyType.Barcode"/>/<see cref="SeparationStrategyType.EveryNPages"/>/
/// <see cref="SeparationStrategyType.Regex"/>/<see cref="SeparationStrategyType.OcrZone"/> — no
/// <see cref="SeparationStrategyType.BlankPage"/>/<see cref="SeparationStrategyType.Similarity"/>, not asked
/// for here), plus a <see cref="BatchMode"/> selector standing in for <c>ImportProfile</c>'s always-on
/// strategy list (since <see cref="BatchMode.NewBatchPerFile"/>/<see cref="BatchMode.Manual"/> are
/// allocator-level policies, not per-page conditions — see <c>BatchAllocator</c>) and a single Indexing
/// Profile picker (batch-level fields have exactly one source profile, unlike <c>ImportProfile</c>'s
/// multi-valid-list).
/// </summary>
public partial class BatchProfileDesignerViewModel : ViewModelBase
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IBatchProfileStore _store;
    private readonly IProfileStore _profileStore;
    private readonly IProfileDialogService _profileDialog;
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

    public BatchProfileDesignerViewModel(
        BatchProfile profile,
        bool isNew,
        IBatchProfileStore store,
        IProfileStore profileStore,
        IProfileDialogService profileDialog,
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
        _profileDialog = profileDialog;
        _dialogs = dialogs;
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _toasts = toasts;
        _barcodes = barcodes;
        _latticeBuilder = latticeBuilder;

        _name = profile.Name;
        _mode = profile.Mode;
        _sampleFileName = profile.SampleFileName;
        _matchMode = profile.MatchMode;
        _matchMinimum = Math.Max(1, profile.MatchMinimum);

        foreach (var strategy in profile.Strategies)
            Strategies.Add(new SeparationStrategyRow(strategy));
    }

    public BatchProfile Profile { get; }

    public bool IsNew { get; }

    public bool Saved { get; private set; }

    public ICommand? CloseCommand { get; set; }

    public IReadOnlyList<BatchMode> ModeOptions { get; } = Enum.GetValues<BatchMode>();

    public IReadOnlyList<SeparationMatchMode> MatchModeOptions { get; } = Enum.GetValues<SeparationMatchMode>();

    // Deliberately not the full SeparationStrategyType enum — BlankPage/Similarity aren't offered for
    // batch boundaries, unlike ImportProfile's document-splitting strategies.
    public IReadOnlyList<SeparationStrategyType> StrategyTypeOptions { get; } =
    [
        SeparationStrategyType.Barcode,
        SeparationStrategyType.EveryNPages,
        SeparationStrategyType.Regex,
        SeparationStrategyType.OcrZone
    ];

    public IReadOnlyList<string> BarcodeFormatOptions => BarcodePatterns.KnownFormats;

    public ObservableCollection<SeparationStrategyRow> Strategies { get; } = [];

    public ObservableCollection<IndexingProfile> IndexingProfileOptions { get; } = [];

    [ObservableProperty]
    private IndexingProfile? _selectedIndexingProfile;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUseStrategies))]
    private BatchMode _mode;

    public bool IsUseStrategies => Mode == BatchMode.UseStrategies;

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
    [NotifyCanExecuteChangedFor(nameof(TestAllStrategiesCommand))]
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

    [ObservableProperty]
    private bool _showOcrWords;

    /// <summary>The current page's recognized OCR/PDF-text words, for the "OCR text" overlay toggle —
    /// same idea as ImportProfileDesignerViewModel's own. Built lazily via <see cref="EnsureLatticeAsync"/>,
    /// not eagerly for every sample page.</summary>
    public IReadOnlyList<LatticeWord> CurrentPageWords =>
        _lattices.TryGetValue(CurrentPageNumber, out var lattice) ? lattice.Words : [];

    partial void OnShowOcrWordsChanged(bool value)
    {
        if (value)
            _ = EnsureLatticeAndNotifyAsync(CurrentPageNumber);
    }

    private async Task EnsureLatticeAndNotifyAsync(int pageNumber)
    {
        await EnsureLatticeAsync(pageNumber).ConfigureAwait(true);
        if (pageNumber == CurrentPageNumber)
            OnPropertyChanged(nameof(CurrentPageWords));
    }

    public async Task InitializeAsync()
    {
        await ReloadIndexingProfileOptionsAsync().ConfigureAwait(true);

        _pageImagePaths.Clear();
        _pageImagePaths.AddRange(GetPageImagePaths());
        SamplePageCount = _pageImagePaths.Count;
        CurrentPageNumber = SamplePageCount == 0 ? 1 : Math.Clamp(CurrentPageNumber, 1, SamplePageCount);
        await ShowPageAsync().ConfigureAwait(true);
        StatusText = SamplePageCount == 0 ? "No sample pages" : string.Empty;

        SelectedStrategy = Strategies.FirstOrDefault();
    }

    // Preserves the current selection (by Id) across a reload — used both by InitializeAsync and after
    // returning from the "New indexing profile…" sub-dialog, so a profile created there shows up
    // immediately without losing anything already selected.
    private async Task ReloadIndexingProfileOptionsAsync()
    {
        var selectedId = SelectedIndexingProfile?.Id ?? Profile.IndexingProfileId;
        IndexingProfileOptions.Clear();
        foreach (var indexingProfile in await _profileStore.GetAllAsync().ConfigureAwait(true))
            IndexingProfileOptions.Add(indexingProfile);

        SelectedIndexingProfile = selectedId is { } id
            ? IndexingProfileOptions.FirstOrDefault(p => p.Id == id)
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
    private void AddStrategy()
    {
        // EveryNPages is the friendliest default to add — it needs no sample/zone at all, so a newly
        // added strategy is immediately usable rather than sitting there needing setup before anything
        // else can happen.
        var row = new SeparationStrategyRow(new SeparationStrategy { Id = Guid.NewGuid(), Type = SeparationStrategyType.EveryNPages });
        Strategies.Add(row);
        SelectedStrategy = row;
        TestAllStrategiesCommand.NotifyCanExecuteChanged();
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
        TestAllStrategiesCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<string> GetPageImagePaths()
    {
        var directory = _paths.BatchProfilePagesDirectory(Profile.Id);
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

    // Deliberately not reusing IProfileSampleService — same reasoning as ImportProfileDesignerViewModel:
    // it's typed to IndexingProfile specifically. BatchProfile only ever needs the rasterized page
    // images; any OCR lattice a strategy card needs is built lazily and separately, see EnsureLatticeAsync.
    private async Task PrepareSampleAsync(string sourcePath)
    {
        _paths.EnsureCreated();
        var pagesDirectory = _paths.BatchProfilePagesDirectory(Profile.Id);

        if (Directory.Exists(pagesDirectory))
            Directory.Delete(pagesDirectory, recursive: true);
        var profileDirectory = _paths.BatchProfileDirectory(Profile.Id);
        if (Directory.Exists(profileDirectory))
        {
            foreach (var stale in Directory.EnumerateFiles(profileDirectory, "sample.*"))
                File.Delete(stale);
        }

        Directory.CreateDirectory(pagesDirectory);

        var originalName = Path.GetFileName(sourcePath);
        var samplePath = _paths.BatchProfileSamplePath(Profile.Id, originalName);
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

        OnPropertyChanged(nameof(CurrentPageWords));
        if (ShowOcrWords)
            _ = EnsureLatticeAndNotifyAsync(CurrentPageNumber);
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
    // config from what's actually there. Fields stay freely editable/clearable afterward — this only
    // ever sets a starting point.
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
            await DetectOcrZoneAsync(row).ConfigureAwait(true);
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
    // preview/test against — built lazily, only when a card actually asks for it. The throwaway
    // CaptureDocument/DocumentPage pair is never persisted, same pattern DocumentImporter uses to build
    // page text/lattices before any real document exists.
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

    // Re-evaluates every strategy against the current sample page and reports each one's own result
    // right next to it in the Test card — unlike DetectAsync (which overwrites a row's fields), this
    // leaves them untouched.
    [RelayCommand(CanExecute = nameof(CanTestAllStrategies))]
    private async Task TestAllStrategiesAsync()
    {
        foreach (var row in Strategies)
            row.TestResult = await TestOneAsync(row).ConfigureAwait(true);
    }

    private bool CanTestAllStrategies() => !IsBusy && Strategies.Count > 0;

    private Task<string> TestOneAsync(SeparationStrategyRow row) => row.Type switch
    {
        SeparationStrategyType.Barcode => Task.FromResult(TestBarcodeMatch(row)),
        SeparationStrategyType.OcrZone => TestOcrZoneMatchAsync(row),
        SeparationStrategyType.Regex => TestRegexMatchAsync(row),
        SeparationStrategyType.EveryNPages => Task.FromResult("Not tested here — depends on this page's position in the sequence, not on this page alone"),
        _ => Task.FromResult("Unknown strategy type")
    };

    private string TestBarcodeMatch(SeparationStrategyRow row)
    {
        if (_barcodes is null || row.Zone is null)
            return "Draw a barcode zone first, then test";

        var pageIndex = CurrentPageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageImagePaths.Count)
            return "No sample page loaded";

        var decoded = _barcodes.Decode(_pageImagePaths[pageIndex], row.Zone);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
            return "No barcode detected in the current zone";

        var formatMatches = string.IsNullOrWhiteSpace(row.BarcodeFormat)
            || string.Equals(row.BarcodeFormat, decoded.Format, StringComparison.OrdinalIgnoreCase);
        var valueMatches = BarcodePatterns.Matches(row.BarcodeValuePattern, decoded.Text);

        return formatMatches && valueMatches
            ? $"Match: {BarcodePatterns.DisplayType(decoded.Format)} “{decoded.Text}”"
            : $"No match — detected {BarcodePatterns.DisplayType(decoded.Format)} “{decoded.Text}”, which doesn't satisfy the current type/value filter";
    }

    // Mirrors SeparationStrategyEvaluator's OcrZone evaluator exactly: an empty pattern matches any
    // non-empty zone text, same as an empty BarcodeValuePattern matches any barcode value.
    private async Task<string> TestOcrZoneMatchAsync(SeparationStrategyRow row)
    {
        if (row.Zone is null)
            return "Draw a zone first, then test";

        var lattice = await EnsureLatticeAsync(CurrentPageNumber).ConfigureAwait(true);
        if (lattice is null)
            return "No text found on this page to test against";

        var extracted = ZonalExtractor.Extract(lattice, row.Zone);
        if (string.IsNullOrWhiteSpace(extracted.Text))
            return "No text detected in the current zone";

        var text = extracted.Text.Trim();
        if (string.IsNullOrWhiteSpace(row.TextPattern))
            return $"Match (any non-empty text): “{text}”";

        return TryRegexMatch(row.TextPattern, extracted.Text)
            ? $"Match: “{text}”"
            : $"No match — detected “{text}”, which doesn't satisfy the current pattern";
    }

    // Mirrors SeparationStrategyEvaluator's Regex evaluator exactly: unlike OcrZone, an empty pattern
    // never hits — there's no other signal to fall back on for a whole-page match.
    private async Task<string> TestRegexMatchAsync(SeparationStrategyRow row)
    {
        if (string.IsNullOrWhiteSpace(row.TextPattern))
            return "Enter a pattern first, then test";

        var lattice = await EnsureLatticeAsync(CurrentPageNumber).ConfigureAwait(true);
        if (lattice is null)
            return "No text found on this page to test against";

        var text = LatticeText.Build(lattice.Words).Text;
        return TryRegexMatch(row.TextPattern, text) ? "Match found on this page" : "No match on this page";
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
            StatusText = "Give this batch profile a name";
            _toasts.ShowError(StatusText);
            return;
        }

        Profile.Name = Name.Trim();
        Profile.Mode = Mode;
        Profile.SampleFileName = SampleFileName;
        Profile.Strategies = Strategies.Select(row => row.ToModel()).ToList();
        Profile.MatchMode = MatchMode;
        Profile.MatchMinimum = Math.Max(1, MatchMinimum);
        Profile.IndexingProfileId = SelectedIndexingProfile?.Id;

        await _store.SaveAsync(Profile).ConfigureAwait(true);
        Saved = true;
        _toasts.ShowSuccess($"Saved \"{Profile.Name}\"");
        CloseCommand?.Execute(null);
    }

    public void Dispose() => SetPageImage(null);
}
