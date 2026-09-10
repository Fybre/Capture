using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
using Capture.Core.CaptureProfiles;
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
    private readonly CaptureWorkflowService _captureWorkflow;
    private readonly IPageManagementService _pageManagement;
    private readonly IFileDialogService _dialogs;
    private readonly IScanSource _scanSource;
    private readonly ProfileExportRunner _exportRunner;
    private readonly ILatticeStore _latticeStore;
    private readonly ILatticeBuilder _latticeBuilder;
    private readonly ICaptureProfileDialogService _captureProfiles;
    private readonly ICaptureProfileStore _captureProfileStore;
    private readonly IIndexValueStore _indexes;
    private readonly IProfileApplicator _profileApplicator;
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
    private readonly IConfirmDialogService _confirm;
    private readonly IExportPdfDialogService _exportPdfDialog;
    private readonly IPdfExportWriter _pdfExportWriter;
    private readonly IFieldScriptRunner? _scripts;
    private readonly IReadOnlyList<IPostIndexStep> _postIndexSteps;

    public MainViewModel(
        IAppPaths paths,
        IDocumentStore store,
        CaptureWorkflowService captureWorkflow,
        IPageManagementService pageManagement,
        IFileDialogService dialogs,
        IScanSource scanSource,
        ProfileExportRunner exportRunner,
        ILatticeStore latticeStore,
        ILatticeBuilder latticeBuilder,
        ICaptureProfileDialogService captureProfiles,
        ICaptureProfileStore captureProfileStore,
        IIndexValueStore indexes,
        IProfileApplicator profileApplicator,
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
        IConfirmDialogService confirm,
        IExportPdfDialogService exportPdfDialog,
        IPdfExportWriter pdfExportWriter,
        IFieldScriptRunner? scripts = null,
        IEnumerable<IPostIndexStep>? postIndexSteps = null)
    {
        _paths = paths;
        _store = store;
        _captureWorkflow = captureWorkflow;
        _pageManagement = pageManagement;
        _dialogs = dialogs;
        _scanSource = scanSource;
        _exportRunner = exportRunner;
        _latticeStore = latticeStore;
        _latticeBuilder = latticeBuilder;
        _captureProfiles = captureProfiles;
        _captureProfileStore = captureProfileStore;
        _indexes = indexes;
        _profileApplicator = profileApplicator;
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
        _confirm = confirm;
        _exportPdfDialog = exportPdfDialog;
        _pdfExportWriter = pdfExportWriter;
        _scripts = scripts;
        _postIndexSteps = postIndexSteps?.ToList() ?? [];
        Documents.CollectionChanged += OnDocumentsChanged;
        SelectedDocuments.CollectionChanged += OnSelectedDocumentsChanged;
        PageThumbnails.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPageThumbnails));
        SelectedPageThumbnails.CollectionChanged += (_, _) => DeleteSelectedPagesCommand.NotifyCanExecuteChanged();
        CaptureProfiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));
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

    // Single source of truth for "something CanActOnSelected/CanActOnTrash/CanMergeSelectedDocuments/
    // CanMarkReady/CanApplyRedactions/CanExport/CanExportAll/CanExportSelectedToPdf reads just changed" —
    // every trigger that affects one of those predicates (IsBusy, ShowTrash, SelectedDocument, the
    // SelectedDocuments collection, ViewMode, the Documents collection) calls this instead of each
    // duplicating its own copy of the same NotifyCanExecuteChanged list. Three separate bugs this session
    // (MarkSelectedReady, then RestoreSelectedTrash/PurgeSelectedTrash) were exactly this: a new bulk-action
    // command added to some but not all of those lists, so it silently never re-evaluated on the one
    // trigger that actually mattered. A command belongs in this method if — and only if — its own
    // CanExecute reads GetActingRows()/SelectedDocument/SelectedDocuments/ShowTrash/Documents.Count; adding
    // one here is now the only step needed, since every trigger already calls this rather than listing
    // commands individually.
    private void RefreshSelectionDependentCommands()
    {
        OnPropertyChanged(nameof(HasSelectedDocuments));
        OnPropertyChanged(nameof(SelectedDocumentsSummary));
        OnPropertyChanged(nameof(HasMultipleSelectedDocuments));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MergeSelectedDocumentsCommand.NotifyCanExecuteChanged();
        RedactSelectedCommand.NotifyCanExecuteChanged();
        ApplyProfileToSelectedCommand.NotifyCanExecuteChanged();
        ApplyRedactionsCommand.NotifyCanExecuteChanged();
        MarkReadyCommand.NotifyCanExecuteChanged();
        MarkSelectedReadyCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ExportAllCommand.NotifyCanExecuteChanged();
        ExportSelectedToPdfCommand.NotifyCanExecuteChanged();
        RestoreSelectedTrashCommand.NotifyCanExecuteChanged();
        PurgeSelectedTrashCommand.NotifyCanExecuteChanged();
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

    // See RefreshSelectionDependentCommands (called from OnSelectedDocumentChanged's body in
    // MainViewModel.Documents.cs) for why this has no NotifyCanExecuteChangedFor attributes of its own.
    [ObservableProperty]
    private DocumentRow? _selectedDocument;

    [ObservableProperty]
    private string _statusText = "Starting…";

    /// <summary>Drives the status text's error styling (see MainWindow.axaml) — set alongside StatusText
    /// wherever it's reporting a failure, so a failure reads visually distinct from routine status
    /// chatter instead of relying on the reader to parse the wording.</summary>
    [ObservableProperty]
    private bool _statusIsError;

    // Selection/view-dependent commands (RemoveSelected, MergeSelectedDocuments,
    // RedactSelected, ApplyProfileToSelected, ApplyRedactions, MarkReady, MarkSelectedReady, Export,
    // ExportAll, ExportSelectedToPdf, RestoreSelectedTrash, PurgeSelectedTrash) are deliberately NOT listed here — see
    // RefreshSelectionDependentCommands, called from OnIsBusyChanged below, for the single place they're
    // all wired instead of duplicating this list. Only genuinely IsBusy-specific commands stay here.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartNewBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCaptureProfilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleManualRedactionModeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveManualRedactionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedPagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SplitDocumentAtCurrentPageCommand))]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshSelectionDependentCommands();
        // A watcher notification can arrive while export, redaction, or another non-import action is
        // busy. Those paths do not pass through ImportPathsAsync's finally block, so resume the queued
        // consumer whenever the application becomes idle.
        if (!value && !_watchProcessing && _watchQueue.Count > 0)
            _ = ProcessWatchQueueAsync();
    }

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
            StatusIsError = false;

            await ApplyWatchAsync().ConfigureAwait(true);
            ViewMode = _watchSettings.StartView;

            // Preview mode needs some document to show, so pick one automatically rather than
            // starting on a blank pane — but only there. Table mode has no such need, and none of its
            // per-group DataGrids reflect this as a highlighted row, so auto-selecting here would
            // silently enable Mark ready/Redact/Remove/Export against a document the user never
            // actually clicked.
            if (Documents.Count > 0 && IsPreviewMode)
                SelectedDocument = Documents[0];

            // Fire-and-forget: a slow/offline/rate-limited GitHub check must never delay startup or
            // the document list appearing. IUpdateCheckService swallows its own failures.
            if (_watchSettings.CheckForUpdatesOnStartup)
                _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task OpenSettingsAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        var result = await _settings.ShowAsync(host);
        if (result.Saved)
        {
            await ApplyWatchAsync();
            // Settings includes the global inbox order. Reload immediately so both currently-visible
            // and hidden views agree without requiring an application restart or another import.
            var selectedId = SelectedDocument?.Id;
            await ReloadDocumentsAsync();
            // Re-finding a genuinely prior selection is fine in any view; manufacturing a new one
            // when nothing was selected is the same phantom-selection bug as InitializeAsync/
            // ImportPathsAsync, so that fallback only applies in Preview mode.
            SelectedDocument = selectedId is { } id
                ? Documents.FirstOrDefault(row => row.Id == id)
                : IsPreviewMode ? Documents.FirstOrDefault() : null;
            OnPropertyChanged(nameof(TableOrderSummary));
        }
        _dialogs.Host = host;
        await LoadProfilesAsync();
        if (result.DocumentsChanged && !result.Saved)
        {
            await ReloadDocumentsAsync();
            if (IsPreviewMode)
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

    private bool CanConfigure() => !IsBusy;

    partial void OnViewModeChanged(WorkspaceMode value)
    {
        // The visible DataGrid can change without either selection property changing immediately, so
        // nudge every selection-dependent command/property when switching views.
        RefreshSelectionDependentCommands();
    }

    private void RefreshRowSelectionFlags()
    {
        foreach (var row in ReviewBatchIndexes.Concat(ReviewDocumentIndexes))
            row.IsSelected = ReferenceEquals(row, SelectedIndex);
        foreach (var row in RedactionCandidates)
            row.IsSelected = ReferenceEquals(row, SelectedRedactionCandidate);
    }
}
