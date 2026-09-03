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

    public bool HasNoDocuments => Documents.Count == 0;

    public ObservableCollection<DocumentRow> SelectedDocuments { get; } = [];

    public bool HasSelectedDocuments => GetActingRows().Count > 0;

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleManualRedactionModeCommand))]
    private Bitmap? _pageImage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkReadyCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkSelectedReadyCommand))]
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
    private string _statusText = "Starting…";

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
    [NotifyCanExecuteChangedFor(nameof(MarkSelectedReadyCommand))]
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

            await ReloadDocumentsAsync().ConfigureAwait(true);

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

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task OpenSettingsAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        var result = await _settings.ShowAsync(host);
        if (result.Saved)
            await ApplyWatchAsync();
        _dialogs.Host = host;
        await LoadProfilesAsync();
        if (result.DocumentsChanged)
        {
            await ReloadDocumentsAsync();
            SelectedDocument = Documents.FirstOrDefault();
        }
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

    private bool CanImport() => !IsBusy;

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
        MarkSelectedReadyCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private void RefreshRowSelectionFlags()
    {
        foreach (var row in ReviewBatchIndexes.Concat(ReviewDocumentIndexes))
            row.IsSelected = ReferenceEquals(row, SelectedIndex);
        foreach (var row in RedactionCandidates)
            row.IsSelected = ReferenceEquals(row, SelectedRedactionCandidate);
    }
}
