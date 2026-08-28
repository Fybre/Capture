using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
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
    private readonly IFileDialogService _dialogs;
    private readonly IScanSource _scanSource;
    private readonly IExportAdapter _exportAdapter;
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
    private readonly IReadOnlyList<IPostIndexStep> _postIndexSteps;
    private readonly Queue<(string Path, WatchFolderEntry Entry)> _watchQueue = new();
    private readonly HashSet<string> _watchQueued = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DocumentPage> _pages = [];
    private int _loadGeneration;
    private WatchSettings _watchSettings = new();
    private bool _restoringProfileSelection;
    private bool _watchProcessing;
    private CaptureBatch? _lastManualBatch;

    public ObservableCollection<BatchProfile> BatchProfiles { get; } = [];

    /// <summary>Batch profile chosen for manual (non-watch-folder) imports — null means today's default,
    /// one new batch per import action.</summary>
    [ObservableProperty]
    private BatchProfile? _selectedBatchProfile;

    public MainViewModel(
        IAppPaths paths,
        IDocumentStore store,
        IDocumentImporter importer,
        IFileDialogService dialogs,
        IScanSource scanSource,
        IExportAdapter exportAdapter,
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
        IEnumerable<IPostIndexStep>? postIndexSteps = null)
    {
        _paths = paths;
        _store = store;
        _importer = importer;
        _dialogs = dialogs;
        _scanSource = scanSource;
        _exportAdapter = exportAdapter;
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
        _postIndexSteps = postIndexSteps?.ToList() ?? [];
        Documents.CollectionChanged += OnDocumentsChanged;
        SelectedDocuments.CollectionChanged += OnSelectedDocumentsChanged;
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));
        _watch.FilesReady += OnWatchFilesReady;
    }

    public ObservableCollection<DocumentRow> Documents { get; } = [];

    public ObservableCollection<IndexingProfile> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public ObservableCollection<IndexValueRow> ReviewBatchIndexes { get; } = [];

    public ObservableCollection<IndexValueRow> ReviewDocumentIndexes { get; } = [];

    public ObservableCollection<DocumentGroupViewModel> DocumentGroups { get; } = [];

    public ObservableCollection<DocumentRow> SelectedDocuments { get; } = [];

    public bool HasSelectedDocuments => GetActingRows().Count > 0;

    public string SelectedDocumentsSummary
    {
        get
        {
            var count = GetActingRows().Count;
            return count == 1 ? "1 selected" : $"{count} selected";
        }
    }

    // In Preview mode there's no multi-select UI — the single InboxGrid selection is the source of
    // truth, read directly here rather than mirrored into SelectedDocuments. A mirror requires staying
    // in lockstep with SelectedDocument across every change, including ones Avalonia's DataGrid makes on
    // its own deferred layout pass when the selected row is removed — any missed sync silently disables
    // "act on selection" commands. Reading SelectedDocument straight from its own property has no such
    // window: whatever it currently holds is authoritative. Table mode keeps using SelectedDocuments,
    // which its grid's own multi-select genuinely drives.
    private IReadOnlyList<DocumentRow> GetActingRows()
    {
        if (IsPreviewMode)
            return SelectedDocument is { } row ? [row] : [];

        return SelectedDocuments.ToList();
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
    private IndexingProfile? _selectedImportProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkReadyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedDocuments))]
    [NotifyPropertyChangedFor(nameof(SelectedDocumentsSummary))]
    private DocumentRow? _selectedDocument;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _currentPageNumber = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    private PageLattice? _currentLattice;

    [ObservableProperty]
    private IReadOnlyList<IndexHighlight> _indexHighlights = [];

    [ObservableProperty]
    private IndexValueRow? _selectedIndex;

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
    [NotifyCanExecuteChangedFor(nameof(MarkReadyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
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
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFilesAsync()
    {
        var files = await _dialogs.PickFilesAsync();
        if (files.Count == 0)
            return;
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
        _ = ShowPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        CurrentPageNumber++;
        _ = ShowPageAsync();
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
                LoadReviewIndexes(SelectedDocument);
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


    [RelayCommand(CanExecute = nameof(CanMarkReady))]
    private async Task MarkReadyAsync()
    {
        if (SelectedDocument is null)
            return;

        SelectedDocument.Document.Status = DocumentStatus.Ready;
        await _store.UpdateAsync(SelectedDocument.Document);
        SelectedDocument.NotifyIndexes();
        StatusText = "Marked ready";
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
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan()
    {
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
    }

    [RelayCommand]
    private void SelectIndexHighlight(Guid id)
    {
        var row = ReviewBatchIndexes.Concat(ReviewDocumentIndexes)
            .FirstOrDefault(item => item.Value.FieldId == id);
        if (row is not null)
            SelectedIndex = row;
    }

    private bool CanImport() => !IsBusy;

    private bool CanMarkReady() =>
        !IsBusy
        && SelectedDocument is not null
        && SelectedDocument.Indexes.Any(index => !index.HideFromIndexing)
        && SelectedDocument.Indexes.Where(index => !index.HideFromIndexing).All(index => !index.IsMissing);

    private bool CanGoPrevious() => !IsBusy && CurrentPageNumber > 1;

    private bool CanGoNext() => !IsBusy && CurrentPageNumber < PageCount;

    private bool CanScan() => _scanSource.IsAvailable && !IsBusy;

    private bool CanExport() => _exportAdapter.IsConfigured && !IsBusy;

    async partial void OnSelectedDocumentChanged(DocumentRow? value)
    {
        LoadReviewIndexes(value);
        await LoadSelectedDocumentAsync(value);
    }

    partial void OnViewModeChanged(WorkspaceMode value)
    {
        // GetActingRows() switches source (SelectedDocument vs. SelectedDocuments) based on ViewMode, so
        // switching views can change what "the current selection" means without either property itself
        // changing — nudge the dependents that don't otherwise know to re-check.
        OnPropertyChanged(nameof(HasSelectedDocuments));
        OnPropertyChanged(nameof(SelectedDocumentsSummary));
        ApplySelectedProfileCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedIndexChanged(IndexValueRow? value)
    {
        if (value is not null && value.Value.PageNumber >= 1 && value.Value.PageNumber != CurrentPageNumber)
        {
            CurrentPageNumber = value.Value.PageNumber;
            _ = ShowPageAsync();
            return;
        }

        RefreshIndexHighlights();
    }

    private async Task ImportPathsAsync(
        IReadOnlyList<string> paths,
        DocumentSource source = DocumentSource.Import,
        IndexingProfile? profile = null,
        string? watchRoot = null,
        WatchFolderEntry? watchFolderEntry = null)
    {
        IsBusy = true;
        try
        {
            profile ??= SelectedImportProfile;

            var batchProfile = watchFolderEntry is not null
                ? (watchFolderEntry.BatchProfileId is { } bpId
                    ? BatchProfiles.FirstOrDefault(item => item.Id == bpId)
                    : null)
                : SelectedBatchProfile;
            var resumeBatch = watchFolderEntry is null && batchProfile?.Trigger == BatchTrigger.Manual
                ? _lastManualBatch
                : null;
            var allocator = await BatchAllocator.CreateAsync(
                _store, batchProfile, watchFolderEntry?.Id, resumeBatch).ConfigureAwait(true);

            var index = 0;
            DocumentRow? last = null;
            var batchSources = new Dictionary<Guid, CaptureDocument>();
            var batchSeparatorValues = new Dictionary<Guid, string?>();
            foreach (var path in paths)
            {
                index++;
                StatusText = $"Importing {index} of {paths.Count}: {Path.GetFileName(path)}";
                try
                {
                    var imported = await _importer.ImportAsync(path, source, profile, batchProfile).ConfigureAwait(true);
                    var failed = false;
                    var isFirstOfFile = true;
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

                    MoveWatchFile(path, watchRoot, success: imported.Count > 0 && !failed);
                }
                catch (Exception ex)
                {
                    StatusText = ex.Message;
                    MoveWatchFile(path, watchRoot, success: false);
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

            if (watchFolderEntry is null && batchProfile?.Trigger == BatchTrigger.Manual)
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

            StatusText = $"Imported {paths.Count} file(s)";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
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
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is not { } app)
            return;

        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private static void MoveWatchFile(string path, string? watchRoot, bool success)
    {
        if (string.IsNullOrWhiteSpace(watchRoot) || !File.Exists(path))
            return;

        try
        {
            WatchFileMover.Move(path, watchRoot, success);
        }
        catch
        {
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
        await RunPostIndexStepsAsync(document, documentValues).ConfigureAwait(true);
    }

    private async Task RunPostIndexStepsAsync(CaptureDocument document, IReadOnlyList<IndexValue> indexValues)
    {
        if (_postIndexSteps.Count == 0)
            return;

        var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
        var context = new PostIndexContext
        {
            Document = document,
            Pages = pages,
            IndexValues = indexValues
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

    private async Task<IReadOnlyList<IndexValue>> ExtractAsync(
        CaptureDocument document,
        IndexingProfile profile,
        string? batchSeparatorValue = null)
    {
        var lattices = new List<PageLattice>();
        for (var page = 1; page <= document.PageCount; page++)
        {
            var lattice = await _latticeStore.GetAsync(document.Id, page).ConfigureAwait(true);
            if (lattice is not null)
                lattices.Add(lattice);
        }

        MacroContext? macro = null;
        if (document.BatchId is { } batchId)
        {
            macro = new MacroContext
            {
                BatchNumber = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true),
                DocumentNumber = await _store.GetDocumentNumberInBatchAsync(batchId, document.Id).ConfigureAwait(true),
                Timestamp = DateTimeOffset.Now
            };
        }

        var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
        return await _applicator.ApplyAsync(profile, lattices, macro, pages, batchSeparatorValue).ConfigureAwait(true);
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

    private IndexValueRow CreateReviewRow(DocumentRow document, IndexValue value)
    {
        return new IndexValueRow(value, document.ConfidenceThreshold, document.Locale)
        {
            Changed = () => _ = PersistReviewAsync(document)
        };
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
            await ShowPageAsync(generation).ConfigureAwait(true);
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
        IndexHighlights = ReviewBatchIndexes.Concat(ReviewDocumentIndexes)
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
            })
            .ToList();
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
    }

    private void OnSelectedDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSelectedDocuments));
        OnPropertyChanged(nameof(SelectedDocumentsSummary));
        ApplySelectedProfileCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }
}
