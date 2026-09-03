using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Capture.App.Services;
using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class ImportProfileDesignerViewModel : ViewModelBase
{
    // A fixed sentinel Id for the single barcode-zone highlight this Designer ever shows — there's no
    // per-field identity here the way ProfileDesignerViewModel's fields have, so SelectHighlight has
    // nothing to resolve back to; the highlight exists purely to render the current BarcodeZone.
    private static readonly Guid BarcodeZoneHighlightId = Guid.NewGuid();

    private readonly IImportProfileStore _store;
    private readonly IProfileStore _profileStore;
    private readonly IFileDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly IToastService _toasts;
    private readonly List<string> _pageImagePaths = [];
    private int _loadGeneration;

    public ImportProfileDesignerViewModel(
        ImportProfile profile,
        bool isNew,
        IImportProfileStore store,
        IProfileStore profileStore,
        IFileDialogService dialogs,
        IAppPaths paths,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        IToastService toasts)
    {
        Profile = profile;
        IsNew = isNew;
        _store = store;
        _profileStore = profileStore;
        _dialogs = dialogs;
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _toasts = toasts;

        _name = profile.Name;
        _trigger = profile.Trigger;
        _pageCount = Math.Max(1, profile.PageCount);
        _blankInkPercent = profile.BlankInkPercent;
        _sampleFileName = profile.SampleFileName;
        _barcodeFormat = profile.BarcodeFormat;
        _barcodeValuePattern = profile.BarcodeValuePattern;
        _discardSeparatorPage = profile.DiscardSeparatorPage;
        _currentPageNumber = Math.Max(1, profile.BarcodePageNumber);
    }

    public ImportProfile Profile { get; }

    public bool IsNew { get; }

    public bool Saved { get; private set; }

    public ICommand? CloseCommand { get; set; }

    public IReadOnlyList<ImportSeparationTrigger> TriggerOptions { get; } = Enum.GetValues<ImportSeparationTrigger>();

    public IReadOnlyList<string> BarcodeFormatOptions => BarcodePatterns.KnownFormats;

    public ObservableCollection<IndexingProfileOption> IndexingProfileOptions { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEveryNPages))]
    [NotifyPropertyChangedFor(nameof(IsBlankPage))]
    [NotifyPropertyChangedFor(nameof(IsBarcode))]
    private ImportSeparationTrigger _trigger;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private int _blankInkPercent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeSampleCommand))]
    private string? _sampleFileName;

    [ObservableProperty]
    private string? _barcodeFormat;

    [ObservableProperty]
    private string? _barcodeValuePattern;

    [ObservableProperty]
    private bool _discardSeparatorPage;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private bool _isBusy;

    public bool IsEveryNPages => Trigger == ImportSeparationTrigger.EveryNPages;

    public bool IsBlankPage => Trigger == ImportSeparationTrigger.BlankPage;

    public bool IsBarcode => Trigger == ImportSeparationTrigger.Barcode;

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
        IndexingProfileOptions.Clear();
        foreach (var indexingProfile in await _profileStore.GetAllAsync().ConfigureAwait(true))
        {
            IndexingProfileOptions.Add(new IndexingProfileOption(indexingProfile)
            {
                IsSelected = Profile.IndexingProfileIds.Contains(indexingProfile.Id)
            });
        }

        _pageImagePaths.Clear();
        _pageImagePaths.AddRange(GetPageImagePaths());
        SamplePageCount = _pageImagePaths.Count;
        CurrentPageNumber = SamplePageCount == 0 ? 1 : Math.Clamp(CurrentPageNumber, 1, SamplePageCount);
        await ShowPageAsync().ConfigureAwait(true);
        StatusText = SamplePageCount == 0 ? "No sample pages" : string.Empty;
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
    // assume an IndexingProfile). ImportProfile only ever needs the rasterized page images (no OCR
    // lattice — there's no field extraction happening here), so this is a smaller, self-contained
    // version rather than widening that service's contract for one extra caller.
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
        var zone = Profile.BarcodeZone;
        Highlights = zone is not null && zone.PageNumber == CurrentPageNumber
            ? [new IndexHighlight
            {
                FieldId = BarcodeZoneHighlightId,
                FieldName = "Barcode zone",
                X = zone.X,
                Y = zone.Y,
                Width = zone.Width,
                Height = zone.Height,
                IsSelected = true
            }]
            : [];
    }

    [RelayCommand]
    private void CompleteZone(NormalizedRect rect)
    {
        if (!IsBarcode || rect.Width < 0.004f || rect.Height < 0.004f)
            return;

        Profile.BarcodeZone = new ZoneRect
        {
            PageNumber = CurrentPageNumber,
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height
        };
        Profile.BarcodePageNumber = CurrentPageNumber;
        RefreshHighlights();
        StatusText = "Barcode zone set";
    }

    [RelayCommand]
    private void ChangeZone(NormalizedRect rect)
    {
        if (!IsBarcode || Profile.BarcodeZone is null)
            return;

        var zone = Profile.BarcodeZone;
        zone.X = Math.Clamp(rect.X, 0, 1);
        zone.Y = Math.Clamp(rect.Y, 0, 1);
        zone.Width = Math.Clamp(rect.Width, 0.002f, 1);
        zone.Height = Math.Clamp(rect.Height, 0.002f, 1);
        zone.PageNumber = CurrentPageNumber;
        Profile.BarcodePageNumber = CurrentPageNumber;
        RefreshHighlights();
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
        Profile.Trigger = Trigger;
        Profile.PageCount = Math.Max(1, PageCount);
        Profile.BlankInkPercent = BlankInkPercent;
        Profile.BarcodeFormat = string.IsNullOrWhiteSpace(BarcodeFormat) ? null : BarcodeFormat;
        Profile.BarcodeValuePattern = string.IsNullOrWhiteSpace(BarcodeValuePattern) ? null : BarcodeValuePattern;
        Profile.DiscardSeparatorPage = DiscardSeparatorPage;
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
