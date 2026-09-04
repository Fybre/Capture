using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
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
    private readonly IBatchProfileStore _batchProfileStore;
    private readonly IProfileDialogService _profileDialog;
    private readonly IBatchProfileDialogService _batchProfileDialog;
    private readonly IFileDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly IToastService _toasts;
    private readonly IBarcodeDecoder? _barcodes;
    private readonly List<string> _pageImagePaths = [];
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
        IBarcodeDecoder? barcodes = null)
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

    public ObservableCollection<BatchProfile> BatchProfileOptions { get; } = [];

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
    private BatchProfile? _selectedBatchProfile;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestMatchCommand))]
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
        await ReloadIndexingProfileOptionsAsync().ConfigureAwait(true);
        await ReloadBatchProfileOptionsAsync().ConfigureAwait(true);

        _pageImagePaths.Clear();
        _pageImagePaths.AddRange(GetPageImagePaths());
        SamplePageCount = _pageImagePaths.Count;
        CurrentPageNumber = SamplePageCount == 0 ? 1 : Math.Clamp(CurrentPageNumber, 1, SamplePageCount);
        await ShowPageAsync().ConfigureAwait(true);
        StatusText = SamplePageCount == 0 ? "No sample pages" : string.Empty;
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
        DetectBarcode();
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
        DetectBarcode();
    }

    // Attempts to decode a real barcode from the zone just drawn/adjusted and pre-fills the
    // type/value fields from what's actually there, since typing a symbology and an exact regex by
    // hand is unnecessary busywork when a real sample is right in front of the user. Both fields stay
    // freely editable/clearable afterward — this only ever sets a starting point, never something the
    // user is locked into.
    private void DetectBarcode()
    {
        if (_barcodes is null || Profile.BarcodeZone is null)
            return;

        var pageIndex = CurrentPageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageImagePaths.Count)
            return;

        var decoded = _barcodes.Decode(_pageImagePaths[pageIndex], Profile.BarcodeZone);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
        {
            StatusText = "Barcode zone set — no barcode detected there; enter the type/value manually if needed";
            return;
        }

        BarcodeFormat = decoded.Format;
        BarcodeValuePattern = $"^{Regex.Escape(decoded.Text)}$";
        StatusText = $"Detected {BarcodePatterns.DisplayType(decoded.Format)}: {decoded.Text}";
    }

    // Re-decodes the current zone and checks it against whatever the user has typed into
    // BarcodeFormat/BarcodeValuePattern right now — unlike DetectBarcode (which overwrites those
    // fields), this leaves them untouched and just reports whether they'd actually match a real
    // scan of this sample. Useful after hand-editing the pattern (e.g. loosening it to a prefix).
    [RelayCommand(CanExecute = nameof(CanTestMatch))]
    private void TestMatch()
    {
        if (Profile.BarcodeZone is null)
        {
            StatusText = "Draw a barcode zone first, then test";
            return;
        }

        var pageIndex = CurrentPageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageImagePaths.Count)
            return;

        var decoded = _barcodes!.Decode(_pageImagePaths[pageIndex], Profile.BarcodeZone);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
        {
            StatusText = "No barcode detected in the current zone";
            return;
        }

        var formatMatches = string.IsNullOrWhiteSpace(BarcodeFormat)
            || string.Equals(BarcodeFormat, decoded.Format, StringComparison.OrdinalIgnoreCase);
        var valueMatches = BarcodePatterns.Matches(BarcodeValuePattern, decoded.Text);

        StatusText = formatMatches && valueMatches
            ? $"Match: {BarcodePatterns.DisplayType(decoded.Format)} “{decoded.Text}”"
            : $"No match — detected {BarcodePatterns.DisplayType(decoded.Format)} “{decoded.Text}”, which doesn't satisfy the current type/value filter";
    }

    private bool CanTestMatch() => !IsBusy && _barcodes is not null;

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
