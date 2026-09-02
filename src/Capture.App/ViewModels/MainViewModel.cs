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

public partial class MainViewModel : ViewModelBase
{
    private readonly IAppPaths _paths;
    private readonly IDocumentStore _store;
    private readonly IDocumentImporter _importer;
    private readonly IPageManagementService _pageManagement;
    private readonly IFileDialogService _dialogs;
    private readonly IScanSource _scanSource;
    private readonly ProfileExportRunner _exportRunner;
    private readonly ILatticeStore _latticeStore;
    private readonly ILatticeBuilder _latticeBuilder;
    private readonly IProfileDialogService _profiles;
    private readonly IBatchProfileDialogService _batchProfiles;
    private readonly IProfileStore _profileStore;
    private readonly IBatchProfileStore _batchProfileStore;
    private readonly IProfileApplicator _applicator;
    private readonly IIndexValueStore _indexes;
    private readonly IWatchFolderService _watch;
    private readonly IWatchSettingsStore _watchStore;
    private readonly IAiFieldCatalogStore _aiCatalogStore;
    private readonly ISettingsDialogService _settings;
    private readonly IHelpWindowService _help;
    private readonly IAboutDialogService _about;
    private readonly IRedactionCandidateStore _redactionCandidates;
    private readonly IRedactionEntitySetStore _redactionSets;
    private readonly RedactionApplier _redactionApplier;
    private readonly RedactionDetectionStep _redactionDetection;
    private readonly PresidioSidecarLauncher _presidioLauncher;
    private readonly IDebugLogService _debugLog;
    private readonly IToastService _toasts;
    private readonly IUpdateCheckService _updateCheck;
    private readonly IFieldScriptRunner? _scripts;
    private readonly IReadOnlyList<IPostIndexStep> _postIndexSteps;
    private readonly Queue<(string Path, WatchFolderEntry Entry)> _watchQueue = new();
    private readonly HashSet<string> _watchQueued = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DocumentPage> _pages = [];
    private int _loadGeneration;
    private WatchSettings _watchSettings = new();
    private bool _restoringProfileSelection;
    private bool _watchProcessing;
    private CaptureBatch? _lastManualBatch;
    private readonly Dictionary<Guid, int> _redactionPersistGenerations = [];

    public ObservableCollection<BatchProfile> BatchProfiles { get; } = [];

    /// <summary>Batch profile chosen for manual (non-watch-folder) imports — null means today's default,
    /// one new batch per import action.</summary>
    [ObservableProperty]
    private BatchProfile? _selectedBatchProfile;

    public MainViewModel(
        IAppPaths paths,
        IDocumentStore store,
        IDocumentImporter importer,
        IPageManagementService pageManagement,
        IFileDialogService dialogs,
        IScanSource scanSource,
        ProfileExportRunner exportRunner,
        ILatticeStore latticeStore,
        ILatticeBuilder latticeBuilder,
        IProfileDialogService profiles,
        IBatchProfileDialogService batchProfiles,
        IProfileStore profileStore,
        IBatchProfileStore batchProfileStore,
        IProfileApplicator applicator,
        IIndexValueStore indexes,
        IWatchFolderService watch,
        IWatchSettingsStore watchStore,
        IAiFieldCatalogStore aiCatalogStore,
        ISettingsDialogService settings,
        IHelpWindowService help,
        IAboutDialogService about,
        IRedactionCandidateStore redactionCandidates,
        IRedactionEntitySetStore redactionSets,
        RedactionApplier redactionApplier,
        RedactionDetectionStep redactionDetection,
        PresidioSidecarLauncher presidioLauncher,
        IDebugLogService debugLog,
        IToastService toasts,
        IUpdateCheckService updateCheck,
        IFieldScriptRunner? scripts = null,
        IEnumerable<IPostIndexStep>? postIndexSteps = null)
    {
        _paths = paths;
        _store = store;
        _importer = importer;
        _pageManagement = pageManagement;
        _dialogs = dialogs;
        _scanSource = scanSource;
        _exportRunner = exportRunner;
        _latticeStore = latticeStore;
        _latticeBuilder = latticeBuilder;
        _profiles = profiles;
        _batchProfiles = batchProfiles;
        _profileStore = profileStore;
        _batchProfileStore = batchProfileStore;
        _applicator = applicator;
        _indexes = indexes;
        _watch = watch;
        _watchStore = watchStore;
        _aiCatalogStore = aiCatalogStore;
        _settings = settings;
        _help = help;
        _about = about;
        _redactionCandidates = redactionCandidates;
        _redactionSets = redactionSets;
        _redactionApplier = redactionApplier;
        _redactionDetection = redactionDetection;
        _presidioLauncher = presidioLauncher;
        _debugLog = debugLog;
        _toasts = toasts;
        _updateCheck = updateCheck;
        _scripts = scripts;
        _postIndexSteps = postIndexSteps?.ToList() ?? [];
        Documents.CollectionChanged += OnDocumentsChanged;
        SelectedDocuments.CollectionChanged += OnSelectedDocumentsChanged;
        SelectedPageThumbnails.CollectionChanged += (_, _) => DeleteSelectedPagesCommand.NotifyCanExecuteChanged();
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));
        RedactionCandidates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRedactionCandidates));
            OnPropertyChanged(nameof(ApplyRedactionsButtonLabel));
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
        };
        _watch.FilesReady += OnWatchFilesReady;
        // A cold sidecar start can take well over a minute — without this, the only feedback during
        // that time would be the generic busy spinner, indistinguishable from a hang. May fire on a
        // background thread, so marshal back to the UI thread before touching StatusText.
        _presidioLauncher.StatusChanged += message => Dispatcher.UIThread.Post(() => StatusText = message);
    }

    public ObservableCollection<DocumentRow> Documents { get; } = [];

    public ObservableCollection<PageThumbnailRow> PageThumbnails { get; } = [];

    public ObservableCollection<PageThumbnailRow> SelectedPageThumbnails { get; } = [];

    public ObservableCollection<IndexingProfile> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public ObservableCollection<IndexValueRow> ReviewBatchIndexes { get; } = [];

    public ObservableCollection<IndexValueRow> ReviewDocumentIndexes { get; } = [];

    public ObservableCollection<RedactionCandidateRow> RedactionCandidates { get; } = [];

    // Deliberately NOT gated on RedactionStatus: the redacted PDF is a derived artifact regenerated
    // from the original pages plus whatever's currently confirmed, not a one-shot irreversible action —
    // so the checklist (and the button below it) stay available to adjust and re-apply even after a
    // document has already been redacted (manually or via the profile's auto-bypass threshold), not
    // just while it's sitting in PendingReview.
    public bool HasRedactionCandidates => RedactionCandidates.Count > 0;

    /// <summary>True once the selected document has an applied, on-disk redacted PDF to show/open —
    /// drives the "Redacted" confirmation panel, shown alongside (not instead of) the still-editable
    /// checklist below it.</summary>
    public bool HasRedactedFile =>
        SelectedDocument?.Document.RedactionStatus == RedactionStatus.Applied
        && !string.IsNullOrEmpty(SelectedDocument.Document.RedactedPath);

    /// <summary>"Apply" the first time; "Re-apply" once a redacted file already exists, since clicking
    /// it again regenerates (overwrites) that file from the current checkbox state.</summary>
    public string ApplyRedactionsButtonLabel => HasRedactedFile
        ? $"Re-apply redactions ({RedactionCandidates.Count})"
        : $"Apply redactions ({RedactionCandidates.Count})";

    public string ManualRedactionButtonLabel => IsAddingManualRedaction
        ? "Done adding redactions"
        : "Add manual redaction";

    public bool HasSelectedManualRedaction => SelectedRedactionCandidate?.IsManual == true;

    public ObservableCollection<DocumentGroupViewModel> DocumentGroups { get; } = [];

    public ObservableCollection<DocumentRow> SelectedDocuments { get; } = [];

    public bool HasSelectedDocuments => GetActingRows().Count > 0;

    public bool HasMultipleSelectedDocuments => SelectedDocuments.Count > 1;

    public string SelectedDocumentsSummary
    {
        get
        {
            var count = GetActingRows().Count;
            return count == 1 ? "1 selected" : $"{count} selected";
        }
    }

    public string RedactSelectedTooltip =>
        "Choose which PII types to detect and redact now, regardless of any profile's Redaction setting.";

    /// <summary>Backs the inline "which redaction set to use" picker opened by the "Redact" toolbar
    /// button — the built-in sets plus whatever custom sets exist, populated fresh each time it opens
    /// since custom sets can change via Settings between uses.</summary>
    public ObservableCollection<RedactionEntitySet> RedactEntitySetOptions { get; } = [];

    [ObservableProperty]
    private RedactionEntitySet? _selectedRedactEntitySet;

    [ObservableProperty]
    private bool _isRedactPickerOpen;

    private IReadOnlyList<DocumentRow> _redactPickerRows = [];

    // Both views mirror their DataGrid multi-selection into SelectedDocuments. SelectedDocument remains
    // the active/anchor row used by the single-document preview; fall back to it for programmatic
    // selections that land before a DataGrid has synchronized its SelectedItems collection.
    private IReadOnlyList<DocumentRow> GetActingRows()
    {
        return SelectedDocuments.Count > 0
            ? SelectedDocuments.ToList()
            : SelectedDocument is { } row ? [row] : [];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewMode))]
    [NotifyPropertyChangedFor(nameof(IsTableMode))]
    private WorkspaceMode _viewMode = WorkspaceMode.Preview;

    public bool IsPreviewMode => ViewMode == WorkspaceMode.Preview;

    public bool IsTableMode => ViewMode == WorkspaceMode.Table;

    public bool HasNoDocuments => Documents.Count == 0;

    public bool HasReviewIndexes => ReviewBatchIndexes.Count > 0 || ReviewDocumentIndexes.Count > 0;

    public bool HasReviewBatchIndexes => ReviewBatchIndexes.Count > 0;

    public bool HasReviewDocumentIndexes => ReviewDocumentIndexes.Count > 0;

    public string PageLabel => PageCount == 0 ? "—" : $"{CurrentPageNumber} / {PageCount}";

    public string PreviewMessage
    {
        get
        {
            if (SelectedDocument is null)
                return "Select a document";
            if (SelectedDocument.Document.Status == DocumentStatus.Error)
                return SelectedDocument.Document.ErrorMessage ?? "Import failed";
            if (PageCount == 0)
                return "No pages";
            return string.Empty;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualRedactionButtonLabel))]
    private bool _isAddingManualRedaction;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleManualRedactionModeCommand))]
    private Bitmap? _pageImage;

    [ObservableProperty]
    private IndexingProfile? _selectedImportProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkReadyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MergeSelectedDocumentsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedactSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRedactionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedDocuments))]
    [NotifyPropertyChangedFor(nameof(SelectedDocumentsSummary))]
    private DocumentRow? _selectedDocument;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(SplitDocumentAtCurrentPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _currentPageNumber = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(SplitDocumentAtCurrentPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageWords))]
    private PageLattice? _currentLattice;

    /// <summary>The current page's recognized OCR/PDF-text words, for the same "Show OCR text" overlay
    /// toggle already used in the Profile Designer — lets a reviewer see exactly where extraction
    /// thinks text is, e.g. when a redaction or index highlight looks misplaced.</summary>
    public IReadOnlyList<LatticeWord> CurrentPageWords => CurrentLattice?.Words ?? [];

    [ObservableProperty]
    private bool _showOcrWords;

    [ObservableProperty]
    private IReadOnlyList<IndexHighlight> _indexHighlights = [];

    [ObservableProperty]
    private IndexValueRow? _selectedIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedManualRedaction))]
    [NotifyCanExecuteChangedFor(nameof(RemoveManualRedactionCommand))]
    private RedactionCandidateRow? _selectedRedactionCandidate;

    [ObservableProperty]
    private string _statusText = "Starting…";

    [ObservableProperty]
    private string _watchStatus = "Watch off";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProfilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MergeSelectedDocumentsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedactSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRedactionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkReadyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleManualRedactionModeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveManualRedactionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedPagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SplitDocumentAtCurrentPageCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanning;

    private CancellationTokenSource? _scanCancellation;

    public void AttachHost(object host)
    {
        _dialogs.Host = host;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _paths.EnsureCreated();
            await _store.InitializeAsync().ConfigureAwait(true);
            AiFieldCatalog.Load(await _aiCatalogStore.LoadAsync().ConfigureAwait(true));
            _watchSettings = await _watchStore.LoadAsync().ConfigureAwait(true);
            await LoadProfilesAsync().ConfigureAwait(true);

            var documents = await _store.GetAllAsync().ConfigureAwait(true);
            Documents.Clear();
            foreach (var document in documents)
                Documents.Add(await CreateRowAsync(document).ConfigureAwait(true));
            RefreshBatchAccents();
            RefreshDocumentGroups();

            StatusText = Documents.Count == 0
                ? "Import a PDF or image to get started"
                : $"{Documents.Count} document(s)";

            if (Documents.Count > 0)
                SelectedDocument = Documents[0];

            await ApplyWatchAsync().ConfigureAwait(true);
            ViewMode = _watchSettings.StartView;

            // Fire-and-forget: a slow/offline/rate-limited GitHub check must never delay startup or
            // the document list appearing. IUpdateCheckService swallows its own failures.
            if (_watchSettings.CheckForUpdatesOnStartup)
                _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var result = await _updateCheck.CheckForUpdateAsync().ConfigureAwait(true);
        if (!result.IsUpdateAvailable)
            return;

        var releaseUrl = result.ReleaseUrl;
        _toasts.ShowInfo(
            $"Capture {result.LatestVersion} is available — click to view the release.",
            releaseUrl is null ? null : () => Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }));
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFilesAsync()
    {
        var files = await _dialogs.PickFilesAsync();
        if (files.Count == 0)
            return;
        await ImportPathsAsync(files);
    }

    /// <summary>Handles file(s)/folder(s) dropped onto the window from Finder/Explorer — the drop
    /// target itself lives in MainWindow's code-behind (OS-level drag-and-drop isn't something a
    /// ViewModel can subscribe to directly); this is where it hands off into the same import pipeline
    /// <see cref="ImportFilesAsync"/> uses, so a drop behaves identically to picking files via the
    /// toolbar button. Folders are expanded one level deep, matching <see cref="ImportFolderAsync"/>.</summary>
    public async Task ImportDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanImport())
            return;

        var files = paths
            .SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path)
                : [path])
            .Where(ImportFormats.IsSupported)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            StatusText = "No supported files in the dropped item(s)";
            return;
        }

        await ImportPathsAsync(files);
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
            return;

        IsBusy = true;
        try
        {
            StatusText = $"Importing folder {folder}…";
            var files = Directory.EnumerateFiles(folder)
                .Where(ImportFormats.IsSupported)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                StatusText = "No supported files in that folder";
                return;
            }

            await ImportPathsAsync(files);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage()
    {
        CurrentPageNumber--;
        // Each navigation gets its own generation, not just each document load — otherwise rapid
        // clicking shares one generation and a slower earlier page load can finish after a faster
        // later one and overwrite the page the user is actually looking at.
        var generation = Interlocked.Increment(ref _loadGeneration);
        _ = ShowPageAsync(generation);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        CurrentPageNumber++;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _ = ShowPageAsync(generation);
    }

    /// <summary>Moves the main preview to the given page — called from the thumbnail strip's
    /// SelectionChanged handler in code-behind when exactly one thumbnail ends up selected (a plain
    /// click, as opposed to a ctrl/shift-click extending a multi-selection for bulk delete).</summary>
    public void JumpToPage(int pageNumber)
    {
        if (pageNumber == CurrentPageNumber)
            return;

        CurrentPageNumber = pageNumber;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _ = ShowPageAsync(generation);
    }

    private bool CanDeleteSelectedPages() =>
        !IsBusy && SelectedPageThumbnails.Count > 0 && SelectedPageThumbnails.Count < PageThumbnails.Count;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedPages))]
    private async Task DeleteSelectedPagesAsync()
    {
        if (SelectedDocument is not { } row)
            return;

        var pageNumbers = SelectedPageThumbnails.Select(item => item.PageNumber).ToList();
        IsBusy = true;
        try
        {
            var updated = await _pageManagement.DeletePagesAsync(row.Id, pageNumbers).ConfigureAwait(true);
            await RefreshDocumentRowInPlaceAsync(row, updated).ConfigureAwait(true);
            RefreshDocumentGroups();
            StatusText = pageNumbers.Count == 1 ? "Deleted 1 page" : $"Deleted {pageNumbers.Count} pages";
            _toasts.ShowSuccess(StatusText);
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

    private bool CanSplitAtCurrentPage() =>
        !IsBusy && SelectedDocument is not null && PageCount > 1 && CurrentPageNumber > 1;

    [RelayCommand(CanExecute = nameof(CanSplitAtCurrentPage))]
    private async Task SplitDocumentAtCurrentPageAsync()
    {
        if (SelectedDocument is not { } row)
            return;

        IsBusy = true;
        try
        {
            var (first, second) = await _pageManagement.SplitDocumentAsync(row.Id, CurrentPageNumber).ConfigureAwait(true);
            var secondRow = await CreateRowAsync(second).ConfigureAwait(true);
            var insertIndex = Documents.IndexOf(row) + 1;
            Documents.Insert(Math.Clamp(insertIndex, 0, Documents.Count), secondRow);
            await RefreshDocumentRowInPlaceAsync(row, first).ConfigureAwait(true);
            RefreshBatchAccents();
            RefreshDocumentGroups();
            StatusText = "Split into two documents";
            _toasts.ShowSuccess(StatusText);
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

    /// <summary>Moves a single page to sit at another page's position, called from the thumbnail strip's
    /// drag-and-drop handler in code-behind — everything after the drop point shifts along by one.</summary>
    public async Task ReorderPagesAsync(int fromPageNumber, int toPageNumber)
    {
        // Unlike DeleteSelectedPagesAsync/SplitDocumentAtCurrentPageAsync, this isn't a [RelayCommand]
        // gated on CanExecute(!IsBusy) — it's called directly from the drop handler in code-behind, so a
        // drop landing mid-operation would otherwise start a second concurrent RewriteDocumentAsync over
        // a stale _pages snapshot with nothing downstream to serialize it. The PageThumbnailStrip is also
        // now disabled (IsEnabled="{Binding !IsBusy}") while busy, so this should be unreachable via the
        // UI; the check stays as a direct guard against the underlying race regardless.
        if (IsBusy || SelectedDocument is not { } row || fromPageNumber == toPageNumber)
            return;

        var newOrder = _pages.Select(page => page.PageNumber).OrderBy(number => number).ToList();
        if (!newOrder.Contains(toPageNumber) || !newOrder.Remove(fromPageNumber))
            return;
        var insertAt = newOrder.IndexOf(toPageNumber);
        newOrder.Insert(insertAt < 0 ? newOrder.Count : insertAt, fromPageNumber);

        IsBusy = true;
        try
        {
            var updated = await _pageManagement.ReorderPagesAsync(row.Id, newOrder).ConfigureAwait(true);
            await RefreshDocumentRowInPlaceAsync(row, updated).ConfigureAwait(true);
            StatusText = "Reordered pages";
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

    /// <summary>Refreshes a document row's state after a page-management operation, mutating the
    /// existing <see cref="DocumentRow"/>/<see cref="CaptureDocument"/> in place rather than swapping in
    /// a new row instance. This is deliberate, not just an optimization: replacing the object at this
    /// row's index in <see cref="Documents"/> makes Avalonia's DataGrid lose track of which row was
    /// selected (it doesn't reliably preserve selection across an ItemsSource element replacement — a
    /// single deferred re-assertion of SelectedItem was tried here and lost the race against the
    /// DataGrid's own later correction, unlike the simpler item-removal case RemoveSelectedAsync already
    /// works around), silently bouncing the selection to another document right after the edit. Keeping
    /// the same object reference selected the whole time sidesteps that entirely.</summary>
    private async Task RefreshDocumentRowInPlaceAsync(DocumentRow row, CaptureDocument updated)
    {
        row.Document.StoredPath = updated.StoredPath;
        row.Document.PageCount = updated.PageCount;
        row.Document.Status = updated.Status;
        row.Document.RedactionStatus = updated.RedactionStatus;
        row.Document.RedactedPath = updated.RedactedPath;
        row.Document.ErrorMessage = updated.ErrorMessage;

        if (updated.ProfileId is { } profileId)
        {
            var profile = await _profileStore.GetAsync(profileId).ConfigureAwait(true);
            if (profile is not null)
            {
                row.ConfidenceThreshold = profile.AutoReadyThreshold;
                row.Locale = profile.Locale;
                row.ProfileName = profile.Name;
            }
        }

        var documentValues = await _indexes.GetAsync(row.Id).ConfigureAwait(true);
        row.SetDocumentIndexes(documentValues);
        if (row.Document.BatchId is { } batchId)
        {
            var batchValues = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
            row.SetBatchIndexes(batchValues);
        }

        row.NotifyIndexes();

        // Since the row object never changed, SelectedDocument never "changes" either, so
        // OnSelectedDocumentChanged's usual reload doesn't fire on its own — do the same reload it
        // would have done, directly, when this is the row currently being previewed.
        if (ReferenceEquals(SelectedDocument, row))
        {
            LoadReviewIndexes(row);
            await LoadRedactionCandidatesAsync(row).ConfigureAwait(true);
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
            await LoadSelectedDocumentAsync(row).ConfigureAwait(true);
        }
    }

    private async Task ApplyProfileToRowAsync(DocumentRow row, IndexingProfile profile)
    {
        await ApplyProfileToDocumentAsync(row.Document, profile, extractBatch: true).ConfigureAwait(true);
        row.ConfidenceThreshold = profile.AutoReadyThreshold;
        row.Locale = profile.Locale;
        row.ProfileName = profile.Name;
        var documentValues = await _indexes.GetAsync(row.Id).ConfigureAwait(true);
        row.SetDocumentIndexes(documentValues);
        await RefreshBatchRowsAsync(row.Document.BatchId).ConfigureAwait(true);
    }

    private bool CanActOnSelected() => !IsBusy && GetActingRows().Count > 0;

    // Manual "redact now" is available on any document regardless of its profile's Redaction.Enabled
    // flag (or even having a profile at all) — Enabled only gates the automatic post-index pipeline.
    // Clicking "Redact" doesn't run detection immediately: it opens an inline picker (RedactEntitySetOptions
    // + IsRedactPickerOpen) so the reviewer can choose which redaction set to use first, since there's
    // no profile config to fall back on to say what should be detected.
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task RedactSelectedAsync()
    {
        _redactPickerRows = GetActingRows();
        if (_redactPickerRows.Count == 0)
            return;

        RedactEntitySetOptions.Clear();
        foreach (var set in BuiltInRedactionSets.All)
            RedactEntitySetOptions.Add(set);
        foreach (var set in await _redactionSets.GetAllAsync().ConfigureAwait(true))
            RedactEntitySetOptions.Add(set);

        SelectedRedactEntitySet = RedactEntitySetOptions.FirstOrDefault(set => set.Id == BuiltInRedactionSets.CoreId);
        IsRedactPickerOpen = true;
    }

    [RelayCommand]
    private void CancelRedact()
    {
        IsRedactPickerOpen = false;
        _redactPickerRows = [];
    }

    [RelayCommand]
    private async Task ConfirmRedactAsync()
    {
        var rows = _redactPickerRows;
        IsRedactPickerOpen = false;
        _redactPickerRows = [];
        if (rows.Count == 0)
            return;

        var entities = SelectedRedactEntitySet?.Entities.ToList() ?? [];

        IsBusy = true;
        try
        {
            var failures = 0;
            foreach (var row in rows)
            {
                if (row.Document.Status == DocumentStatus.Error)
                    continue;

                try
                {
                    var settings = new RedactionSettings { Entities = entities };
                    var pages = await _store.GetPagesAsync(row.Id).ConfigureAwait(true);
                    await _redactionDetection.DetectAsync(row.Document, pages, row.Indexes, settings).ConfigureAwait(true);
                    row.NotifyIndexes();
                }
                catch (Exception ex)
                {
                    failures++;
                    Trace.TraceError($"Manual redaction failed for document {row.Id}: {ex}");
                }
            }

            if (SelectedDocument is not null && rows.Contains(SelectedDocument))
            {
                await LoadRedactionCandidatesAsync(SelectedDocument).ConfigureAwait(true);
                RefreshIndexHighlights();
                ApplyRedactionsCommand.NotifyCanExecuteChanged();
            }

            StatusText = failures == 0
                ? $"Redaction checked for {rows.Count} document(s)"
                : $"Redaction checked for {rows.Count} document(s) — {failures} failed";
            if (failures == 0) _toasts.ShowSuccess(StatusText); else _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task ApplySelectedProfileAsync(IndexingProfile? profile)
    {
        var rows = GetActingRows();
        if (profile is null || rows.Count == 0)
            return;

        IsBusy = true;
        try
        {
            foreach (var row in rows)
                await ApplyProfileToRowAsync(row, profile).ConfigureAwait(true);
            if (SelectedDocument is not null && rows.Contains(SelectedDocument))
            {
                LoadReviewIndexes(SelectedDocument);
                // ApplyProfileToRowAsync can trigger automatic redaction (if the profile has it
                // enabled) via the post-index pipeline — reload candidates so a fresh detection shows
                // up immediately instead of only after reselecting the document.
                await LoadRedactionCandidatesAsync(SelectedDocument).ConfigureAwait(true);
                ApplyRedactionsCommand.NotifyCanExecuteChanged();
            }

            RefreshIndexHighlights();
            RefreshDocumentGroups();
            StatusText = $"Applied {profile.Name} to {rows.Count} document(s)";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task RemoveSelectedAsync()
    {
        var rows = GetActingRows();
        if (rows.Count == 0)
            return;

        IsBusy = true;
        try
        {
            foreach (var row in rows)
            {
                await _store.DeleteAsync(row.Id).ConfigureAwait(true);
                Documents.Remove(row);
                SelectedDocuments.Remove(row);
            }

            RefreshBatchAccents();
            RefreshDocumentGroups();

            if (IsPreviewMode)
            {
                // Avalonia's DataGrid clears its own SelectedItem on a deferred layout pass when the
                // currently-selected row is removed from ItemsSource — that pass can run *after* this
                // method returns and silently stomp SelectedDocument back to null. Posting at Loaded
                // priority runs after that pass settles, so our reselection wins instead of losing the race.
                // Guard against a second, unrelated change (e.g. a subsequent import) landing in between:
                // only apply this reselection if nothing else has touched SelectedDocument since.
                var expected = SelectedDocument;
                var next = Documents.FirstOrDefault();
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(SelectedDocument, expected))
                        SelectedDocument = next;
                }, DispatcherPriority.Loaded);
            }

            StatusText = $"Removed {rows.Count} document(s)";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMergeSelectedDocuments() => !IsBusy && GetActingRows().Count > 1;

    [RelayCommand(CanExecute = nameof(CanMergeSelectedDocuments))]
    private async Task MergeSelectedDocumentsAsync()
    {
        var rows = SelectedDocuments.ToList();
        if (rows.Count < 2)
            return;

        IsBusy = true;
        try
        {
            var targetRow = rows[0];
            var merged = await _pageManagement.MergeDocumentsAsync(rows.Select(row => row.Id).ToList())
                .ConfigureAwait(true);

            if (_lastManualBatch is { } openBatch
                && rows.Skip(1).Any(row => row.Document.BatchId == openBatch.Id))
            {
                _lastManualBatch = merged.BatchId is { } batchId
                    ? new CaptureBatch
                    {
                        Id = batchId,
                        Number = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true)
                    }
                    : null;
            }

            foreach (var absorbed in rows.Skip(1))
                Documents.Remove(absorbed);
            await RefreshDocumentRowInPlaceAsync(targetRow, merged).ConfigureAwait(true);

            SelectedDocuments.Clear();
            SelectedDocuments.Add(targetRow);
            SelectedDocument = targetRow;
            RefreshBatchAccents();
            RefreshDocumentGroups();
            StatusText = $"Merged {rows.Count} documents into {merged.PageCount} pages";
            _toasts.ShowSuccess(StatusText);
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


    [RelayCommand(CanExecute = nameof(CanMarkReady))]
    private async Task MarkReadyAsync()
    {
        if (SelectedDocument is null)
            return;

        // This can now trigger automatic redaction (a cold Presidio start can take well over a
        // minute — see PresidioSidecarLauncher), so it needs the same busy indication as any other
        // potentially-slow action, not just an instantaneous status flip.
        IsBusy = true;
        try
        {
            var document = SelectedDocument.Document;
            document.Status = DocumentStatus.Ready;
            await _store.UpdateAsync(document);
            SelectedDocument.NotifyIndexes();
            StatusText = "Marked ready";

            // Reaching Ready by manual override should trigger the same post-index steps (redaction,
            // etc.) as reaching it automatically through indexing — otherwise a document only gets
            // those side effects depending on how it got to Ready, which isn't a distinction the user
            // meant to make.
            if (document.ProfileId is { } profileId
                && await _profileStore.GetAsync(profileId).ConfigureAwait(true) is { } profile)
            {
                var indexes = await _indexes.GetAsync(document.Id).ConfigureAwait(true);
                var batchValues = document.BatchId is { } batchId
                    ? await _indexes.GetBatchAsync(batchId).ConfigureAwait(true)
                    : [];
                await RunPostIndexStepsAsync(document, batchValues.Concat(indexes).ToList(), profile).ConfigureAwait(true);

                // RunPostIndexStepsAsync can trigger automatic redaction — reload candidates so a
                // fresh detection shows up immediately instead of only after reselecting the document.
                if (SelectedDocument?.Document.Id == document.Id)
                {
                    await LoadRedactionCandidatesAsync(SelectedDocument).ConfigureAwait(true);
                    RefreshIndexHighlights();
                    ApplyRedactionsCommand.NotifyCanExecuteChanged();
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task OpenProfilesAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        await _profiles.ShowAsync(host);
        _dialogs.Host = host;
        await LoadProfilesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task OpenBatchProfilesAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        await _batchProfiles.ShowAsync(host);
        _dialogs.Host = host;
        await LoadBatchProfilesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task OpenSettingsAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        if (await _settings.ShowAsync(host))
            await ApplyWatchAsync();
        _dialogs.Host = host;
        await LoadProfilesAsync();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        var host = _dialogs.Host;
        if (host is not null)
            _help.Show(host);
    }

    [RelayCommand]
    private async Task OpenAboutAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        await _about.ShowAsync(host);
        _dialogs.Host = host;
    }

    [RelayCommand]
    private void ClearBatchProfile() => SelectedBatchProfile = null;

    [RelayCommand]
    private void ClearImportProfile() => SelectedImportProfile = null;

    [RelayCommand]
    private void ShowPreviewMode() => ViewMode = WorkspaceMode.Preview;

    [RelayCommand]
    private void ShowTableMode() => ViewMode = WorkspaceMode.Table;

    [RelayCommand]
    private void OpenInPreview(DocumentRow? row)
    {
        if (row is null)
            return;
        SelectedDocument = row;
        ViewMode = WorkspaceMode.Preview;
        // The Inbox grid is hidden while in Table mode, and Avalonia's DataGrid doesn't reliably sync
        // its own SelectedItem highlight/scroll-into-view from a binding assigned while it wasn't
        // visible — reassert the selection once the mode switch's layout pass has made it visible.
        Dispatcher.UIThread.Post(() => SelectedDocument = row, DispatcherPriority.Loaded);
    }

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

    private enum ExportOutcome { Exported, ExportedAndRemoved, Skipped, Failed }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var selected = GetActingRows();
        await RunExportAsync(selected.Count > 0 ? selected : Documents.ToList()).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanExportAll))]
    private async Task ExportAllAsync() => await RunExportAsync(Documents.ToList()).ConfigureAwait(true);

    private bool CanExport() => !IsBusy && Documents.Count > 0;

    private bool CanExportAll() => !IsBusy && Documents.Count > 0;

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

        if (profile.RemoveAfterExport)
        {
            await _store.DeleteAsync(document.Id).ConfigureAwait(true);
            Documents.Remove(row);
            SelectedDocuments.Remove(row);
            return ExportOutcome.ExportedAndRemoved;
        }

        document.Status = DocumentStatus.Exported;
        await _store.UpdateAsync(document).ConfigureAwait(true);
        row.NotifyIndexes();
        return ExportOutcome.Exported;
    }

    [RelayCommand]
    private void SelectIndexHighlight(Guid id)
    {
        var row = ReviewBatchIndexes.Concat(ReviewDocumentIndexes)
            .FirstOrDefault(item => item.Value.FieldId == id);
        if (row is not null)
        {
            SelectedIndex = row;
            return;
        }

        var candidateRow = RedactionCandidates.FirstOrDefault(item => item.Id == id);
        if (candidateRow is not null)
        {
            SelectedRedactionCandidate = candidateRow;
            RefreshIndexHighlights();
        }
    }

    private bool CanImport() => !IsBusy;

    private bool CanMarkReady() =>
        !IsBusy
        && SelectedDocument is not null
        && SelectedDocument.Indexes.Any(index => !index.HideFromIndexing && !index.IsReadOnly)
        && SelectedDocument.Indexes.Where(index => !index.HideFromIndexing && !index.IsReadOnly).All(index => !index.IsMissing);

    private bool CanGoPrevious() => !IsBusy && CurrentPageNumber > 1;

    private bool CanGoNext() => !IsBusy && CurrentPageNumber < PageCount;

    private bool CanScan() => _scanSource.IsAvailable && !IsBusy;

    async partial void OnSelectedDocumentChanged(DocumentRow? value)
    {
        IsAddingManualRedaction = false;
        LoadReviewIndexes(value);
        await LoadRedactionCandidatesAsync(value);
        ApplyRedactionsCommand.NotifyCanExecuteChanged();
        await LoadSelectedDocumentAsync(value);
    }

    partial void OnViewModeChanged(WorkspaceMode value)
    {
        // The visible DataGrid can change without either selection property changing immediately, so
        // nudge every selection-dependent command/property when switching views.
        OnPropertyChanged(nameof(HasSelectedDocuments));
        OnPropertyChanged(nameof(SelectedDocumentsSummary));
        OnPropertyChanged(nameof(HasMultipleSelectedDocuments));
        ApplySelectedProfileCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MergeSelectedDocumentsCommand.NotifyCanExecuteChanged();
        RedactSelectedCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedIndexChanged(IndexValueRow? value)
    {
        // Selecting an index field (via a click on its row or on its highlight in the preview) and a
        // redaction candidate are mutually exclusive — only one thing is ever "the selected thing".
        if (value is not null)
            SelectedRedactionCandidate = null;
        RefreshRowSelectionFlags();

        if (value is not null && value.Value.PageNumber >= 1 && value.Value.PageNumber != CurrentPageNumber)
        {
            CurrentPageNumber = value.Value.PageNumber;
            _ = ShowPageAsync();
            return;
        }

        RefreshIndexHighlights();
    }

    partial void OnSelectedRedactionCandidateChanged(RedactionCandidateRow? value)
    {
        if (value is not null)
            SelectedIndex = null;
        RefreshRowSelectionFlags();

        if (value is not null && value.PageNumber >= 1 && value.PageNumber != CurrentPageNumber)
        {
            CurrentPageNumber = value.PageNumber;
            _ = ShowPageAsync();
            return;
        }

        RefreshIndexHighlights();
    }

    partial void OnIsAddingManualRedactionChanged(bool value)
    {
        if (value)
            StatusText = "Draw a rectangle on the page; select a manual rectangle to move or resize it";
    }

    private void RefreshRowSelectionFlags()
    {
        foreach (var row in ReviewBatchIndexes.Concat(ReviewDocumentIndexes))
            row.IsSelected = ReferenceEquals(row, SelectedIndex);
        foreach (var row in RedactionCandidates)
            row.IsSelected = ReferenceEquals(row, SelectedRedactionCandidate);
    }

    private async Task ImportPathsAsync(
        IReadOnlyList<string> paths,
        DocumentSource source = DocumentSource.Import,
        IndexingProfile? profile = null,
        string? watchRoot = null,
        WatchFolderEntry? watchFolderEntry = null,
        IReadOnlyDictionary<string, int>? imageDpiByPath = null,
        bool manageBusy = true)
    {
        if (manageBusy)
            IsBusy = true;
        try
        {
            profile ??= SelectedImportProfile;

            var batchProfile = watchFolderEntry is not null
                ? (watchFolderEntry.BatchProfileId is { } bpId
                    ? BatchProfiles.FirstOrDefault(item => item.Id == bpId)
                    : null)
                : SelectedBatchProfile;
            var keepsBatchOpen = batchProfile is null || batchProfile.Trigger == BatchTrigger.Manual;
            var resumeBatch = watchFolderEntry is null && keepsBatchOpen
                ? _lastManualBatch
                : null;
            var allocator = await BatchAllocator.CreateAsync(
                _store, batchProfile, watchFolderEntry?.Id, resumeBatch).ConfigureAwait(true);

            var index = 0;
            DocumentRow? last = null;
            var batchSources = new Dictionary<Guid, CaptureDocument>();
            var batchSeparatorValues = new Dictionary<Guid, string?>();
            var failedFiles = 0;
            foreach (var path in paths)
            {
                index++;
                StatusText = $"Importing {index} of {paths.Count}: {Path.GetFileName(path)}";
                try
                {
                    var dpi = imageDpiByPath?.GetValueOrDefault(path);
                    var imported = await _importer.ImportAsync(
                            path, source, profile, batchProfile, imageDpiOverride: dpi)
                        .ConfigureAwait(true);
                    var (fileLast, failed) = await MaterializeImportedAsync(
                            imported, profile, allocator, batchSources, batchSeparatorValues, isFirstOfFile: true)
                        .ConfigureAwait(true);
                    if (fileLast is not null)
                        last = fileLast;

                    if (imported.Count == 0 || failed)
                        failedFiles++;
                    MoveWatchFile(path, watchRoot, watchFolderEntry, success: imported.Count > 0 && !failed);
                }
                catch (Exception ex)
                {
                    failedFiles++;
                    StatusText = ex.Message;
                    MoveWatchFile(path, watchRoot, watchFolderEntry, success: false);
                }
            }

            if (profile is not null)
            {
                foreach (var (batchId, batchSource) in batchSources)
                {
                    await ApplyBatchFieldsAsync(batchSource, profile, batchSeparatorValues.GetValueOrDefault(batchId))
                        .ConfigureAwait(true);
                    await RefreshBatchRowsAsync(batchId).ConfigureAwait(true);
                }
            }

            if (watchFolderEntry is null && keepsBatchOpen)
                _lastManualBatch = allocator.Current;

            RefreshBatchAccents();
            RefreshDocumentGroups();
            if (last is not null)
            {
                SelectedDocument = last;
                // SelectedDocument's setter kicks off LoadSelectedDocumentAsync via an async-void
                // On...Changed handler, which this method does not otherwise wait for. Without this
                // explicit await, IsBusy (which gates page-navigation commands) can flip back to
                // false in the finally below while that background load is still in flight, leaving
                // Previous/NextPage's CanExecute settled against a stale PageCount for this document.
                await LoadSelectedDocumentAsync(last).ConfigureAwait(true);
            }

            StatusText = failedFiles == 0
                ? $"Imported {paths.Count} file(s)"
                : failedFiles == paths.Count
                    ? $"Import failed for all {paths.Count} file(s)"
                    : $"Imported {paths.Count - failedFiles} of {paths.Count} file(s) — {failedFiles} failed";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (manageBusy)
                IsBusy = false;
            if (manageBusy && !_watchProcessing && _watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
    }

    /// <summary>Batch-allocates, applies profile fields to, and creates a row for each document produced
    /// by one import call — shared by <see cref="ImportPathsAsync"/>'s per-file loop and
    /// <see cref="ImportScannedPagesAsync"/>'s single scan-job import, so the two entry points can't
    /// drift apart on how a resulting <see cref="ImportedDocument"/> gets surfaced.</summary>
    private async Task<(DocumentRow? Last, bool Failed)> MaterializeImportedAsync(
        IReadOnlyList<ImportedDocument> imported,
        IndexingProfile? profile,
        BatchAllocator allocator,
        Dictionary<Guid, CaptureDocument> batchSources,
        Dictionary<Guid, string?> batchSeparatorValues,
        bool isFirstOfFile)
    {
        DocumentRow? last = null;
        var failed = false;
        foreach (var item in imported)
        {
            var document = item.Document;
            var batch = await allocator.NextAsync(isFirstOfFile, item.StartsNewBatch, document.PageCount)
                .ConfigureAwait(true);
            isFirstOfFile = false;

            document.BatchId = batch.Id;
            await _store.UpdateAsync(document).ConfigureAwait(true);
            await ApplyDocumentFieldsAsync(document, profile, item.SeparatorValues, item.BatchSeparatorValue)
                .ConfigureAwait(true);
            if (!batchSources.ContainsKey(batch.Id) && document.Status != DocumentStatus.Error)
            {
                batchSources[batch.Id] = document;
                batchSeparatorValues[batch.Id] = item.BatchSeparatorValue;
            }
            last = await CreateRowAsync(document).ConfigureAwait(true);
            Documents.Add(last);
            if (document.Status == DocumentStatus.Error)
                failed = true;
        }

        return (last, failed);
    }

    private async Task ImportScannedPagesAsync(IReadOnlyList<ScannedPageInfo> pages, DocumentSource source)
    {
        try
        {
            var profile = SelectedImportProfile;
            var batchProfile = SelectedBatchProfile;
            var keepsBatchOpen = batchProfile is null || batchProfile.Trigger == BatchTrigger.Manual;
            var resumeBatch = keepsBatchOpen ? _lastManualBatch : null;
            var allocator = await BatchAllocator.CreateAsync(_store, batchProfile, watchFolderEntryId: null, resumeBatch)
                .ConfigureAwait(true);

            var batchSources = new Dictionary<Guid, CaptureDocument>();
            var batchSeparatorValues = new Dictionary<Guid, string?>();
            StatusText = "Importing scanned pages…";

            IReadOnlyList<ImportedDocument> imported;
            try
            {
                imported = await _importer.ImportScannedPagesAsync(pages, source, profile, batchProfile)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusText = $"Scan import failed: {ex.Message}";
                return;
            }

            var (last, failed) = await MaterializeImportedAsync(
                    imported, profile, allocator, batchSources, batchSeparatorValues, isFirstOfFile: true)
                .ConfigureAwait(true);

            if (profile is not null)
            {
                foreach (var (batchId, batchSource) in batchSources)
                {
                    await ApplyBatchFieldsAsync(batchSource, profile, batchSeparatorValues.GetValueOrDefault(batchId))
                        .ConfigureAwait(true);
                    await RefreshBatchRowsAsync(batchId).ConfigureAwait(true);
                }
            }

            if (keepsBatchOpen)
                _lastManualBatch = allocator.Current;

            RefreshBatchAccents();
            RefreshDocumentGroups();
            if (last is not null)
            {
                SelectedDocument = last;
                await LoadSelectedDocumentAsync(last).ConfigureAwait(true);
            }

            StatusText = imported.Count == 0
                ? "Scan produced no pages"
                : failed
                    ? "Scan imported with errors"
                    : $"Imported {imported.Count} document(s) from scan";
        }
        finally
        {
            if (!_watchProcessing && _watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
    }

    private void OnWatchFilesReady(WatchFolderEntry entry, IReadOnlyList<string> files)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var file in files)
            {
                var resolved = string.IsNullOrWhiteSpace(entry.Folder) ? null : WatchPaths.Resolve(file, entry.Folder);
                if (resolved is null)
                    continue;
                if (_watchQueued.Add(resolved))
                    _watchQueue.Enqueue((resolved, entry));
            }

            _ = ProcessWatchQueueAsync();
        });
    }

    private async Task ProcessWatchQueueAsync()
    {
        if (_watchProcessing || IsBusy || _watchQueue.Count == 0)
            return;

        _watchProcessing = true;
        try
        {
            while (_watchQueue.Count > 0)
            {
                var pending = new List<(string Path, WatchFolderEntry Entry)>();
                while (_watchQueue.Count > 0)
                    pending.Add(_watchQueue.Dequeue());

                // Files queued from different watch folders carry different profiles/roots —
                // import each folder's files as its own batch rather than mixing them together.
                foreach (var group in pending.GroupBy(item => item.Entry.Id))
                {
                    var entry = group.First().Entry;
                    var batch = new List<string>();
                    foreach (var (path, _) in group)
                    {
                        if (File.Exists(path))
                            batch.Add(path);
                        else
                            _watchQueued.Remove(path);
                    }

                    if (batch.Count == 0)
                        continue;

                    var profile = entry.ProfileId is { } id
                        ? Profiles.FirstOrDefault(item => item.Id == id)
                        : null;
                    await ImportPathsAsync(batch, DocumentSource.Watch, profile, entry.Folder, entry);
                    foreach (var path in batch)
                        _watchQueued.Remove(path);
                }
            }
        }
        finally
        {
            _watchProcessing = false;
            if (_watchQueue.Count > 0)
                _ = ProcessWatchQueueAsync();
        }
    }

    private async Task ApplyWatchAsync()
    {
        _watchSettings = await _watchStore.LoadAsync().ConfigureAwait(true);
        _watch.Apply(_watchSettings.WatchFolders);
        var active = _watch.ActiveFolders;
        WatchStatus = active.Count switch
        {
            0 => "Watch off",
            1 => $"Watching {active[0].Folder}",
            _ => $"Watching {active.Count} folders"
        };
        ApplyTheme(_watchSettings.Theme);
        _debugLog.SetEnabled(_watchSettings.DebugMode);
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is not { } app)
            return;

        app.RequestedThemeVariant = ThemeVariantMapper.Map(theme);
    }

    private void MoveWatchFile(string path, string? watchRoot, WatchFolderEntry? watchFolderEntry, bool success)
    {
        if (string.IsNullOrWhiteSpace(watchRoot) || !File.Exists(path))
            return;

        try
        {
            WatchFileMover.Move(path, watchRoot, success);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to file away watch file '{path}': {ex}");
            var fileName = Path.GetFileName(path);
            if (watchFolderEntry is null)
            {
                StatusText = $"Couldn't file away {fileName}: {ex.Message}";
                return;
            }

            StatusText = _watch.ReportFailure(watchFolderEntry, path)
                ? $"Couldn't file away {fileName} ({ex.Message}) — will retry"
                : $"Couldn't file away {fileName} after repeated attempts ({ex.Message}) — left in the watch folder, needs manual attention";
        }
    }

    private async Task ApplyProfileToDocumentAsync(
        CaptureDocument document,
        IndexingProfile profile,
        bool extractBatch)
    {
        await ApplyDocumentFieldsAsync(document, profile).ConfigureAwait(true);
        if (extractBatch)
            await ApplyBatchFieldsAsync(document, profile).ConfigureAwait(true);
    }

    private async Task ApplyDocumentFieldsAsync(
        CaptureDocument document,
        IndexingProfile? profile,
        IReadOnlyDictionary<Guid, string>? separatorValues = null,
        string? batchSeparatorValue = null)
    {
        if (profile is null || document.Status == DocumentStatus.Error)
            return;

        var extracted = await ExtractAsync(document, profile, batchSeparatorValue).ConfigureAwait(true);
        if (separatorValues is { Count: > 0 })
        {
            foreach (var value in extracted)
            {
                if (!string.IsNullOrWhiteSpace(value.Value) || !separatorValues.TryGetValue(value.FieldId, out var seeded))
                    continue;
                value.Value = seeded;
                value.Confidence = Math.Max(value.Confidence, 95);
            }
        }

        var documentValues = extracted.Where(value => value.Level != IndexLevel.Batch).ToList();
        await _indexes.SaveAsync(document.Id, documentValues).ConfigureAwait(true);
        document.ProfileId = profile.Id;
        var batchValues = document.BatchId is { } batchId
            ? await _indexes.GetBatchAsync(batchId).ConfigureAwait(true)
            : [];
        document.Status = IndexFormat.StatusFor(batchValues.Concat(documentValues), profile.AutoReadyThreshold);
        await _store.UpdateAsync(document).ConfigureAwait(true);
        await RunPostIndexStepsAsync(document, batchValues.Concat(documentValues).ToList(), profile).ConfigureAwait(true);
    }

    private async Task RunPostIndexStepsAsync(CaptureDocument document, IReadOnlyList<IndexValue> indexValues, IndexingProfile profile)
    {
        if (_postIndexSteps.Count == 0)
            return;

        var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
        var context = new PostIndexContext
        {
            Document = document,
            Pages = pages,
            IndexValues = indexValues,
            Profile = profile
        };

        foreach (var step in _postIndexSteps)
        {
            try
            {
                await step.RunAsync(context).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Post-index step {step.GetType().Name} failed for document {document.Id}: {ex}");
            }
        }
    }

    private async Task ApplyBatchFieldsAsync(CaptureDocument document, IndexingProfile profile, string? batchSeparatorValue = null)
    {
        if (document.BatchId is not { } batchId || document.Status == DocumentStatus.Error)
            return;

        var extracted = await ExtractAsync(document, profile, batchSeparatorValue).ConfigureAwait(true);
        var batchValues = extracted.Where(value => value.Level == IndexLevel.Batch).ToList();

        if (string.IsNullOrEmpty(batchSeparatorValue))
        {
            // No freshly captured batch-trigger value this time (e.g. a manual "apply profile" re-run,
            // which has no barcode/regex hit to seed from). A BatchSeparatorValue field has no other way
            // to derive its value, so preserve whatever a real import already captured for it rather than
            // blanking it out — but only that field kind: any other batch-level field (zone/pattern-based)
            // can legitimately re-extract as empty, and that new result should win, not a stale one.
            var separatorFieldIds = profile.Fields
                .Where(field => field.Kind == FieldKind.BatchSeparatorValue)
                .Select(field => field.Id)
                .ToHashSet();
            if (separatorFieldIds.Count > 0)
            {
                var existing = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
                foreach (var value in batchValues)
                {
                    if (separatorFieldIds.Contains(value.FieldId)
                        && string.IsNullOrWhiteSpace(value.Value)
                        && existing.FirstOrDefault(item => item.FieldId == value.FieldId) is { } previous
                        && !string.IsNullOrWhiteSpace(previous.Value))
                        value.Value = previous.Value;
                }
            }
        }

        await _indexes.SaveBatchAsync(batchId, batchValues).ConfigureAwait(true);
    }

    /// <summary>Every page's already-built lattice for a document — used both for real extraction
    /// (ExtractAsync) and to build a script's Document.Text (RunButtonFieldAsync). Assumes lattices
    /// already exist (built during import); a page with none simply isn't included, same as before this
    /// was extracted into its own method.</summary>
    private async Task<List<PageLattice>> LoadAllLatticesAsync(CaptureDocument document)
    {
        var lattices = new List<PageLattice>();
        for (var page = 1; page <= document.PageCount; page++)
        {
            var lattice = await _latticeStore.GetAsync(document.Id, page).ConfigureAwait(true);
            if (lattice is not null)
                lattices.Add(lattice);
        }

        return lattices;
    }

    private async Task<IReadOnlyList<IndexValue>> ExtractAsync(
        CaptureDocument document,
        IndexingProfile profile,
        string? batchSeparatorValue = null)
    {
        var lattices = await LoadAllLatticesAsync(document).ConfigureAwait(true);

        DefaultValueContext? context = null;
        var existingValues = new List<IndexValue>(await _indexes.GetAsync(document.Id).ConfigureAwait(true));
        if (document.BatchId is { } batchId)
        {
            context = new DefaultValueContext
            {
                BatchNumber = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true),
                DocumentNumber = await _store.GetDocumentNumberInBatchAsync(batchId, document.Id).ConfigureAwait(true),
                Timestamp = DateTimeOffset.Now
            };
            existingValues.AddRange(await _indexes.GetBatchAsync(batchId).ConfigureAwait(true));
        }

        var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
        return await _applicator.ApplyAsync(profile, lattices, context, pages, batchSeparatorValue, existingValues, document)
            .ConfigureAwait(true);
    }

    public async Task MoveDocumentToBatchAsync(Guid documentId, Guid batchId)
    {
        var row = Documents.FirstOrDefault(item => item.Id == documentId);
        if (row is null)
            return;

        var oldBatch = row.Document.BatchId;
        if (oldBatch == batchId)
            return;

        row.Document.BatchId = batchId;
        await _store.UpdateAsync(row.Document).ConfigureAwait(true);
        if (oldBatch is { } previous)
            await _store.DeleteEmptyBatchAsync(previous).ConfigureAwait(true);

        var batchValues = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
        row.SetBatchIndexes(batchValues);
        await _store.UpdateAsync(row.Document).ConfigureAwait(true);
        PlaceInBatch(row, batchId);
        RefreshBatchAccents();
        RefreshDocumentGroups();
        LoadReviewIndexes(row);
        RefreshIndexHighlights();
        StatusText = "Moved to another batch";
    }

    private async Task RefreshBatchRowsAsync(Guid? batchId)
    {
        if (batchId is not { } id)
            return;

        var batchValues = await _indexes.GetBatchAsync(id).ConfigureAwait(true);
        foreach (var row in Documents.Where(item => item.Document.BatchId == id))
        {
            row.SetBatchIndexes(batchValues);
            await _store.UpdateAsync(row.Document).ConfigureAwait(true);
        }
    }

    private async Task<DocumentRow> CreateRowAsync(CaptureDocument document)
    {
        var row = new DocumentRow(document);
        if (document.ProfileId is { } profileId)
        {
            var profile = await _profileStore.GetAsync(profileId).ConfigureAwait(true);
            if (profile is not null)
            {
                row.ConfidenceThreshold = profile.AutoReadyThreshold;
                row.Locale = profile.Locale;
                row.ProfileName = profile.Name;
            }
        }

        var values = await _indexes.GetAsync(document.Id).ConfigureAwait(true);
        if (values.Count > 0)
            row.SetDocumentIndexes(values);
        if (document.BatchId is { } batchId)
        {
            var batchValues = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
            if (batchValues.Count > 0)
                row.SetBatchIndexes(batchValues);
        }

        return row;
    }

    private async Task LoadProfilesAsync()
    {
        // Suppress the change-triggered persist below while restoring both selections from settings —
        // SelectedImportProfile is set here and SelectedBatchProfile a moment later inside
        // LoadBatchProfilesAsync, each firing its own fire-and-forget PersistLastProfilesAsync. Without
        // this guard, the first of those can race the second and write a half-restored (null batch
        // profile) state to disk before the second call corrects it.
        _restoringProfileSelection = true;
        try
        {
            var restoreId = SelectedImportProfile?.Id ?? _watchSettings.LastImportProfileId;
            Profiles.Clear();
            foreach (var profile in await _profileStore.GetAllAsync().ConfigureAwait(true))
                Profiles.Add(profile);

            SelectedImportProfile = restoreId is { } id
                ? Profiles.FirstOrDefault(profile => profile.Id == id)
                : null;

            await LoadBatchProfilesAsync().ConfigureAwait(true);
        }
        finally
        {
            _restoringProfileSelection = false;
        }
    }

    private async Task LoadBatchProfilesAsync()
    {
        var restoreId = SelectedBatchProfile?.Id ?? _watchSettings.LastBatchProfileId;
        BatchProfiles.Clear();
        foreach (var profile in await _batchProfileStore.GetAllAsync().ConfigureAwait(true))
            BatchProfiles.Add(profile);

        SelectedBatchProfile = restoreId is { } id
            ? BatchProfiles.FirstOrDefault(profile => profile.Id == id)
            : null;
    }

    partial void OnSelectedImportProfileChanged(IndexingProfile? value)
    {
        if (!_restoringProfileSelection)
            _ = PersistLastProfilesAsync();
    }

    partial void OnSelectedBatchProfileChanged(BatchProfile? value)
    {
        if (!_restoringProfileSelection)
            _ = PersistLastProfilesAsync();
    }

    private async Task PersistLastProfilesAsync()
    {
        if (_watchSettings.LastImportProfileId == SelectedImportProfile?.Id
            && _watchSettings.LastBatchProfileId == SelectedBatchProfile?.Id)
            return;

        _watchSettings.LastImportProfileId = SelectedImportProfile?.Id;
        _watchSettings.LastBatchProfileId = SelectedBatchProfile?.Id;
        await _watchStore.SaveAsync(_watchSettings).ConfigureAwait(true);
    }

    private void LoadReviewIndexes(DocumentRow? row)
    {
        ClearReview(ReviewBatchIndexes);
        ClearReview(ReviewDocumentIndexes);
        SelectedIndex = null;

        if (row is null)
        {
            OnPropertyChanged(nameof(HasReviewIndexes));
            OnPropertyChanged(nameof(HasReviewBatchIndexes));
            OnPropertyChanged(nameof(HasReviewDocumentIndexes));
            return;
        }

        foreach (var value in row.BatchIndexes.Where(index => !index.HideFromIndexing))
            ReviewBatchIndexes.Add(CreateReviewRow(row, value));
        foreach (var value in row.DocumentIndexes.Where(index => !index.HideFromIndexing))
            ReviewDocumentIndexes.Add(CreateReviewRow(row, value));

        OnPropertyChanged(nameof(HasReviewIndexes));
        OnPropertyChanged(nameof(HasReviewBatchIndexes));
        OnPropertyChanged(nameof(HasReviewDocumentIndexes));
        MarkReadyCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadRedactionCandidatesAsync(DocumentRow? row)
    {
        RedactionCandidates.Clear();
        SelectedRedactionCandidate = null;

        if (row is not null)
        {
            var candidates = await _redactionCandidates.GetAsync(row.Id).ConfigureAwait(true);
            foreach (var candidate in candidates)
                RedactionCandidates.Add(CreateRedactionCandidateRow(candidate));
        }

        OnPropertyChanged(nameof(HasRedactionCandidates));
        OnPropertyChanged(nameof(HasRedactedFile));
        OnPropertyChanged(nameof(ApplyRedactionsButtonLabel));
    }

    private RedactionCandidateRow CreateRedactionCandidateRow(RedactionCandidate candidate)
    {
        var candidateRow = new RedactionCandidateRow(candidate);
        candidateRow.Selected = () => SelectedRedactionCandidate = candidateRow;
        candidateRow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RedactionCandidateRow.IsConfirmed))
                return;
            RefreshIndexHighlights();
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
        };
        return candidateRow;
    }

    [RelayCommand(CanExecute = nameof(CanToggleManualRedactionMode))]
    private void ToggleManualRedactionMode() => IsAddingManualRedaction = !IsAddingManualRedaction;

    private bool CanToggleManualRedactionMode() =>
        !IsBusy && SelectedDocument is not null && PageImage is not null;

    [RelayCommand]
    private void AddManualRedaction(NormalizedRect rect)
    {
        if (!IsAddingManualRedaction || SelectedDocument is null
            || rect.Width < 0.004f || rect.Height < 0.004f)
            return;

        var candidate = new RedactionCandidate
        {
            Source = RedactionSource.Manual,
            Label = "Manual redaction",
            PageNumber = CurrentPageNumber,
            X = Math.Clamp(rect.X, 0, 1),
            Y = Math.Clamp(rect.Y, 0, 1),
            Width = Math.Clamp(rect.Width, 0.002f, 1),
            Height = Math.Clamp(rect.Height, 0.002f, 1),
            Score = 1f,
            Decision = RedactionDecision.Confirmed
        };
        var row = CreateRedactionCandidateRow(candidate);
        RedactionCandidates.Add(row);
        SelectedRedactionCandidate = row;
        RefreshIndexHighlights();
        SchedulePersistRedactionCandidates();
        StatusText = "Manual redaction added; draw another or click Done adding redactions";
    }

    [RelayCommand]
    private void ChangeManualRedaction(NormalizedRect rect)
    {
        if (!IsAddingManualRedaction || SelectedRedactionCandidate is not { IsManual: true } row)
            return;

        row.Candidate.X = Math.Clamp(rect.X, 0, 1);
        row.Candidate.Y = Math.Clamp(rect.Y, 0, 1);
        row.Candidate.Width = Math.Clamp(rect.Width, 0.002f, 1);
        row.Candidate.Height = Math.Clamp(rect.Height, 0.002f, 1);
        RefreshIndexHighlights();
        SchedulePersistRedactionCandidates();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManualRedaction))]
    private void RemoveManualRedaction()
    {
        if (SelectedRedactionCandidate is not { IsManual: true } row)
            return;

        RedactionCandidates.Remove(row);
        SelectedRedactionCandidate = null;
        RefreshIndexHighlights();
        SchedulePersistRedactionCandidates();
        StatusText = "Manual redaction removed";
    }

    private bool CanRemoveManualRedaction() =>
        !IsBusy && SelectedRedactionCandidate?.IsManual == true;

    private void SchedulePersistRedactionCandidates()
    {
        if (SelectedDocument is null)
            return;

        var documentId = SelectedDocument.Id;
        var candidates = RedactionCandidates.Select(row => row.Candidate).ToList();
        var generation = _redactionPersistGenerations.TryGetValue(documentId, out var current)
            ? current + 1
            : 1;
        _redactionPersistGenerations[documentId] = generation;
        _ = PersistRedactionCandidatesAfterDelayAsync(documentId, candidates, generation);
    }

    private async Task PersistRedactionCandidatesAfterDelayAsync(
        Guid documentId,
        IReadOnlyList<RedactionCandidate> candidates,
        int generation)
    {
        await Task.Delay(200).ConfigureAwait(true);
        if (!_redactionPersistGenerations.TryGetValue(documentId, out var latest) || latest != generation)
            return;

        try
        {
            await _redactionCandidates.SaveAsync(documentId, candidates).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't save redaction edits: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenRedactedFile()
    {
        var path = SelectedDocument?.Document.RedactedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            StatusText = "Redacted file not found on disk";
            return;
        }

        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{path}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{path}\"");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the redacted file: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyRedactions))]
    private async Task ApplyRedactionsAsync()
    {
        if (SelectedDocument is null || RedactionCandidates.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var document = SelectedDocument.Document;
            var candidates = RedactionCandidates.Select(row => row.Candidate).ToList();
            _redactionPersistGenerations[document.Id] =
                _redactionPersistGenerations.TryGetValue(document.Id, out var generation) ? generation + 1 : 1;
            await _redactionCandidates.SaveAsync(document.Id, candidates).ConfigureAwait(true);

            var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
            await _redactionApplier.ApplyAsync(document, pages, candidates).ConfigureAwait(true);

            // The checklist stays populated (and editable) after this — applying doesn't "use up" the
            // candidates, it just regenerates the redacted file from whatever's currently confirmed, so
            // rejecting a false positive and clicking the button again is exactly how you fix one.
            SelectedDocument.NotifyIndexes();
            RefreshIndexHighlights();
            OnPropertyChanged(nameof(HasRedactedFile));
            OnPropertyChanged(nameof(ApplyRedactionsButtonLabel));
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
            StatusText = document.RedactionStatus == RedactionStatus.Applied
                ? $"Redacted PDF saved to {document.RedactedPath}"
                : $"Redaction failed: {document.RedactionError}";
            if (document.RedactionStatus == RedactionStatus.Applied) _toasts.ShowSuccess(StatusText); else _toasts.ShowError(StatusText);
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

    private bool CanApplyRedactions() =>
        !IsBusy && SelectedDocument is not null && RedactionCandidates.Count > 0;

    private IndexValueRow CreateReviewRow(DocumentRow document, IndexValue value)
    {
        var row = new IndexValueRow(value, document.ConfidenceThreshold, document.Locale, _scripts?.IsAvailable ?? false)
        {
            Changed = () => _ = PersistReviewAsync(document)
        };
        row.Selected = () => SelectedIndex = row;
        return row;
    }

    /// <summary>Runs a Button field's attached script — the review panel's on-demand counterpart to
    /// AfterFieldsPopulated profile scripts. Full read/write over every field on the document (unlike a
    /// Script-kind field's own read-only expression), gated on WatchSettings.AllowFieldScripts exactly
    /// like real import/export, since this is running someone else's (the profile author's) script for
    /// whoever happens to be reviewing, not the author testing their own work interactively — that
    /// interactive exception is the Designer's "Run test" only.</summary>
    [RelayCommand]
    private async Task RunButtonFieldAsync(IndexValueRow row)
    {
        if (SelectedDocument is not { } documentRow || row.Value.Kind != FieldKind.Button)
            return;

        if (_scripts is null || !_scripts.IsAvailable)
        {
            StatusText = "Scripting is off — turn on \"Allow profile scripts\" in Settings";
            _toasts.ShowError(StatusText);
            return;
        }

        var document = documentRow.Document;
        var profile = document.ProfileId is { } profileId ? Profiles.FirstOrDefault(item => item.Id == profileId) : null;
        var field = profile?.Fields.FirstOrDefault(item => item.Id == row.Value.FieldId);
        if (field is null || string.IsNullOrWhiteSpace(field.ButtonScriptSource))
        {
            StatusText = "This button has no script configured";
            _toasts.ShowError(StatusText);
            return;
        }

        row.IsRunning = true;
        try
        {
            var lattices = await LoadAllLatticesAsync(document).ConfigureAwait(true);
            DefaultValueContext? defaultContext = null;
            if (document.BatchId is { } batchId)
            {
                defaultContext = new DefaultValueContext
                {
                    BatchNumber = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true),
                    DocumentNumber = await _store.GetDocumentNumberInBatchAsync(batchId, document.Id).ConfigureAwait(true),
                    Timestamp = DateTimeOffset.Now
                };
            }

            var context = new ScriptExecutionContext
            {
                ProfileName = profile!.Name,
                DocumentNumber = defaultContext?.DocumentNumber ?? 1,
                BatchNumber = defaultContext?.BatchNumber ?? 1,
                Timestamp = DateTimeOffset.Now,
                Values = documentRow.Indexes,
                Document = ScriptDocumentInfo.From(lattices, document)
            };

            // The real field's Id, not a fresh Guid — so RoslynFieldScriptRunner's compiled-script
            // cache (keyed on id + source hash) is actually reused across repeated clicks.
            var script = new FieldScript
            {
                Id = field.Id,
                Name = field.Name,
                Source = field.ButtonScriptSource,
                TimeoutSeconds = field.ButtonTimeoutSeconds
            };

            var result = await _scripts.RunProfileScriptAsync(script, context, sharedSource: profile.SharedScriptSource).ConfigureAwait(true);
            if (!result.Success)
            {
                Trace.TraceError($"Button script \"{field.Name}\" failed: {result.ErrorMessage}");
                StatusText = $"Script failed: {result.ErrorMessage}";
                _toasts.ShowError(StatusText);
                return;
            }

            await PersistReviewAsync(documentRow).ConfigureAwait(true);
            // A button script can write to any field, not just its own — refresh every currently
            // visible row's cached display state rather than tracking which ones actually changed.
            foreach (var visibleRow in ReviewBatchIndexes.Concat(ReviewDocumentIndexes))
                visibleRow.Refresh();
            StatusText = "Script ran successfully";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            row.IsRunning = false;
        }
    }

    /// <summary>Copies one Document-level field's current value into the same field on every other
    /// currently-selected document — for correcting/setting a shared value (e.g. "Supplier") across a
    /// batch of documents without opening each one individually. Batch-level fields are excluded: they
    /// already propagate to every document in their batch automatically via PersistReviewAsync, so
    /// "applying to the selection" would be redundant (and ambiguous across documents from different
    /// batches).</summary>
    [RelayCommand]
    private async Task ApplyFieldToSelectionAsync(IndexValueRow row)
    {
        if (SelectedDocument is not { } source || row.IsBatch)
            return;

        var targets = SelectedDocuments.Where(item => item.Id != source.Id).ToList();
        if (targets.Count == 0)
            return;

        var applied = 0;
        foreach (var target in targets)
        {
            var match = target.DocumentIndexes.FirstOrDefault(item => item.FieldId == row.Value.FieldId);
            if (match is null)
                continue;

            match.Value = row.Value.Value;
            match.IsManual = true;
            match.Confidence = 100;
            match.ValidationError = IndexFormat.Validate(match.Value, match.Format, target.Locale);
            await PersistReviewAsync(target).ConfigureAwait(true);
            applied++;
        }

        StatusText = applied > 0
            ? $"Applied \"{row.Name}\" to {applied} other document{(applied == 1 ? "" : "s")}"
            : "No other selected documents have this field";
        if (applied > 0)
            _toasts.ShowSuccess(StatusText);
        else
            _toasts.ShowError(StatusText);
    }

    private static void ClearReview(ObservableCollection<IndexValueRow> rows)
    {
        foreach (var item in rows)
            item.Changed = null;
        rows.Clear();
    }

    private async Task PersistReviewAsync(DocumentRow row)
    {
        try
        {
            await _indexes.SaveAsync(row.Id, row.DocumentIndexes).ConfigureAwait(true);
            if (row.Document.BatchId is { } batchId)
            {
                await _indexes.SaveBatchAsync(batchId, row.BatchIndexes).ConfigureAwait(true);
                foreach (var other in Documents.Where(item => item.Document.BatchId == batchId && item.Id != row.Id))
                {
                    other.SetBatchIndexes(row.BatchIndexes);
                    await _store.UpdateAsync(other.Document).ConfigureAwait(true);
                }
            }

            row.RecalcStatus();
            await _store.UpdateAsync(row.Document).ConfigureAwait(true);
            row.NotifyIndexes();
            RefreshIndexHighlights();
            MarkReadyCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task LoadSelectedDocumentAsync(DocumentRow? row)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        _pages = [];
        PageCount = 0;
        CurrentPageNumber = 1;
        CurrentLattice = null;
        IndexHighlights = [];
        SetPageImage(null);
        PageThumbnails.Clear();
        SelectedPageThumbnails.Clear();
        OnPropertyChanged(nameof(PreviewMessage));

        if (row is null)
            return;

        try
        {
            var pages = await _store.GetPagesAsync(row.Id).ConfigureAwait(true);
            if (generation != _loadGeneration)
                return;

            _pages = pages;
            PageCount = pages.Count;
            CurrentPageNumber = pages.Count == 0 ? 1 : 1;
            foreach (var page in pages)
                PageThumbnails.Add(new PageThumbnailRow(page));
            await ShowPageAsync(generation).ConfigureAwait(true);
            _ = LoadPageThumbnailsAsync(pages, generation);
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration)
                StatusText = ex.Message;
        }
        finally
        {
            OnPropertyChanged(nameof(PreviewMessage));
        }
    }

    private const int ThumbnailPixelWidth = 120;

    private async Task LoadPageThumbnailsAsync(IReadOnlyList<DocumentPage> pages, int generation)
    {
        foreach (var page in pages)
        {
            if (generation != _loadGeneration)
                return;
            if (!File.Exists(page.ImagePath))
                continue;

            Bitmap thumbnail;
            try
            {
                thumbnail = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(page.ImagePath);
                    return Bitmap.DecodeToWidth(stream, ThumbnailPixelWidth);
                }).ConfigureAwait(true);
            }
            catch (Exception)
            {
                continue; // skip an unreadable page's thumbnail rather than failing the whole strip
            }

            if (generation != _loadGeneration)
            {
                thumbnail.Dispose();
                return;
            }

            var thumbnailRow = PageThumbnails.FirstOrDefault(item => item.PageNumber == page.PageNumber);
            if (thumbnailRow is not null)
                thumbnailRow.Thumbnail = thumbnail;
            else
                thumbnail.Dispose();
        }
    }

    private async Task ShowPageAsync(int? generation = null)
    {
        generation ??= _loadGeneration;
        var page = _pages.FirstOrDefault(item => item.PageNumber == CurrentPageNumber);
        if (page is null || !File.Exists(page.ImagePath))
        {
            SetPageImage(null);
            OnPropertyChanged(nameof(PreviewMessage));
            return;
        }

        var bitmap = await Task.Run(() =>
        {
            using var stream = File.OpenRead(page.ImagePath);
            return new Bitmap(stream);
        }).ConfigureAwait(true);

        if (generation != _loadGeneration)
        {
            bitmap.Dispose();
            return;
        }

        SetPageImage(bitmap);
        await LoadLatticeAsync(page, generation.Value).ConfigureAwait(true);
        RefreshIndexHighlights();
        OnPropertyChanged(nameof(PreviewMessage));
    }

    private async Task LoadLatticeAsync(DocumentPage page, int generation)
    {
        if (SelectedDocument is null)
        {
            CurrentLattice = null;
            return;
        }

        var lattice = await _latticeStore.GetAsync(SelectedDocument.Id, page.PageNumber).ConfigureAwait(true);
        if (generation != _loadGeneration)
            return;

        if (lattice is null)
        {
            try
            {
                StatusText = $"Reading page {page.PageNumber}…";
                lattice = await _latticeBuilder.BuildPageAsync(SelectedDocument.Document, page).ConfigureAwait(true);
                await _latticeStore.SaveAsync(SelectedDocument.Id, lattice).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (generation == _loadGeneration)
                    StatusText = ex.Message;
                return;
            }
        }

        if (generation != _loadGeneration)
            return;

        CurrentLattice = lattice;
    }

    private void RefreshIndexHighlights()
    {
        var indexHighlights = ReviewBatchIndexes.Concat(ReviewDocumentIndexes)
            .Where(item => item.Value.Bounds is not null && item.Value.PageNumber == CurrentPageNumber)
            .Select(item => new IndexHighlight
            {
                FieldId = item.Value.FieldId,
                FieldName = item.Value.FieldName,
                X = item.Value.Bounds!.X,
                Y = item.Value.Bounds.Y,
                Width = item.Value.Bounds.Width,
                Height = item.Value.Bounds.Height,
                IsSelected = SelectedIndex?.Value.FieldId == item.Value.FieldId,
                CanEdit = false
            });

        var redactionHighlights = RedactionCandidates
            .Where(row => row.PageNumber == CurrentPageNumber)
            .Select(row => new IndexHighlight
            {
                FieldId = row.Id,
                FieldName = row.Label,
                X = row.Candidate.X,
                Y = row.Candidate.Y,
                Width = row.Candidate.Width,
                Height = row.Candidate.Height,
                IsSelected = SelectedRedactionCandidate?.Id == row.Id,
                CanEdit = row.IsManual,
                IsRedaction = true,
                IsRejected = !row.IsConfirmed
            });

        IndexHighlights = indexHighlights.Concat(redactionHighlights).ToList();
    }

    private void SetPageImage(Bitmap? bitmap)
    {
        var previous = PageImage;
        PageImage = bitmap;
        previous?.Dispose();
    }

    private void PlaceInBatch(DocumentRow row, Guid batchId)
    {
        Documents.Remove(row);
        var last = -1;
        for (var i = 0; i < Documents.Count; i++)
        {
            if (Documents[i].Document.BatchId == batchId)
                last = i;
        }

        if (last >= 0)
            Documents.Insert(last + 1, row);
        else
            Documents.Add(row);
    }

    private void RefreshBatchAccents()
    {
        Guid? previous = null;
        var accent = false;
        foreach (var row in Documents)
        {
            var batchId = row.Document.BatchId;
            if (batchId != previous)
            {
                if (previous is not null)
                    accent = !accent;
                previous = batchId;
            }

            row.BatchAccent = accent;
        }
    }

    private void RefreshDocumentGroups()
    {
        var groups = new List<DocumentGroupViewModel>();

        foreach (var byProfile in Documents.Where(row => row.Document.ProfileId is not null)
                     .GroupBy(row => row.Document.ProfileId!.Value))
        {
            var profile = Profiles.FirstOrDefault(item => item.Id == byProfile.Key);
            var documents = byProfile.OrderBy(row => row.FileName, StringComparer.OrdinalIgnoreCase).ToList();

            IReadOnlyList<string> batchFieldNames;
            IReadOnlyList<string> documentFieldNames;
            if (profile is not null)
            {
                batchFieldNames = profile.Fields
                    .Where(field => !field.HideFromIndexing && field.Level == IndexLevel.Batch)
                    .Select(field => field.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                documentFieldNames = profile.Fields
                    .Where(field => !field.HideFromIndexing && field.Level == IndexLevel.Document)
                    .Select(field => field.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                // Profile was deleted after being applied — fall back to whatever fields the documents actually carry.
                batchFieldNames = documents
                    .SelectMany(row => row.BatchIndexes)
                    .Where(value => !value.HideFromIndexing)
                    .Select(value => value.FieldName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                documentFieldNames = documents
                    .SelectMany(row => row.DocumentIndexes)
                    .Where(value => !value.HideFromIndexing)
                    .Select(value => value.FieldName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            groups.Add(new DocumentGroupViewModel
            {
                Title = profile?.Name ?? "Unknown profile",
                IsUnassigned = false,
                BatchFieldNames = batchFieldNames,
                DocumentFieldNames = documentFieldNames,
                Documents = documents
            });
        }

        groups = groups.OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase).ToList();

        var unassigned = Documents.Where(row => row.Document.ProfileId is null)
            .OrderBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unassigned.Count > 0)
        {
            groups.Add(new DocumentGroupViewModel
            {
                Title = "No profile applied",
                IsUnassigned = true,
                BatchFieldNames = [],
                DocumentFieldNames = [],
                Documents = unassigned
            });
        }

        DocumentGroups.Clear();
        foreach (var group in groups)
            DocumentGroups.Add(group);
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNoDocuments));
        ExportCommand.NotifyCanExecuteChanged();
        ExportAllCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectedDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSelectedDocuments));
        OnPropertyChanged(nameof(SelectedDocumentsSummary));
        OnPropertyChanged(nameof(HasMultipleSelectedDocuments));
        ApplySelectedProfileCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MergeSelectedDocumentsCommand.NotifyCanExecuteChanged();
        RedactSelectedCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }
}
