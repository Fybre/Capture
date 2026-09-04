using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Store;
using Capture.Core.Watch;
using Capture.LocalAi;
using Capture.Scanner;
using Capture.Storage;
using Capture.Therefore;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IFileDialogService _dialogs;
    private readonly IWatchSettingsStore _store;
    private readonly IProfileStore _profiles;
    private readonly IImportProfileStore _importProfiles;
    private readonly IAiFieldCatalogStore _catalogStore;
    private readonly IRedactionEntitySetStore _redactionEntitySets;
    private readonly IScanSource _scanSource;
    private readonly IAppPaths _paths;
    private readonly IThereforeClient _thereforeClient;
    private readonly ILocalAiModelDownloader _localAiModelDownloader;
    private readonly IToastService _toasts;
    private readonly IConfirmDialogService _confirm;
    private readonly IDocumentStore _documents;

    /// <summary>Exposed so SettingsWindow's code-behind can attach/detach the nested Therefore
    /// connection dialog as a toast host — that window is opened directly from a Click handler with
    /// no DI access of its own, and this ViewModel already has the service.</summary>
    public IToastService Toasts => _toasts;

    public SettingsViewModel(
        IFileDialogService dialogs,
        IWatchSettingsStore store,
        IProfileStore profiles,
        IImportProfileStore importProfiles,
        IAiFieldCatalogStore catalogStore,
        IRedactionEntitySetStore redactionEntitySets,
        IScanSource scanSource,
        IAppPaths paths,
        IThereforeClient thereforeClient,
        ILocalAiModelDownloader localAiModelDownloader,
        IToastService toasts,
        IConfirmDialogService confirm,
        IDocumentStore documents)
    {
        _dialogs = dialogs;
        _store = store;
        _profiles = profiles;
        _importProfiles = importProfiles;
        _catalogStore = catalogStore;
        _redactionEntitySets = redactionEntitySets;
        _scanSource = scanSource;
        _paths = paths;
        _thereforeClient = thereforeClient;
        _localAiModelDownloader = localAiModelDownloader;
        _toasts = toasts;
        _confirm = confirm;
        _documents = documents;
        WatchFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWatchFolders));
        RefreshLocalAiModelStatus();
    }

    public bool HasWatchFolders => WatchFolders.Count > 0;

    public string AiFieldCatalogPath => _paths.AiFieldCatalogPath;

    public IReadOnlyList<WorkspaceMode> StartViewOptions { get; } = Enum.GetValues<WorkspaceMode>();

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = Enum.GetValues<AppTheme>();

    public IReadOnlyList<NoBatchProfileBehavior> NoBatchProfileBehaviorOptions { get; } = Enum.GetValues<NoBatchProfileBehavior>();

    public IReadOnlyList<DuplicateImportBehavior> DuplicateImportBehaviorOptions { get; } = Enum.GetValues<DuplicateImportBehavior>();

    public ObservableCollection<IndexingProfile> Profiles { get; } = [];


    public ObservableCollection<ImportProfile> ImportProfiles { get; } = [];

    public bool Saved { get; private set; }

    /// <summary>True once CleanUpOldDocumentsAsync has actually deleted something — tells
    /// MainViewModel (via SettingsDialogResult) that its own in-memory document list is now stale and
    /// needs reloading, distinct from Saved (which only covers the Save button/WatchSettings).</summary>
    public bool DocumentsChanged { get; private set; }

    public Action? Close { get; set; }

    [ObservableProperty]
    private WorkspaceMode _startView = WorkspaceMode.Preview;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private NoBatchProfileBehavior _noBatchProfileBehavior = NoBatchProfileBehavior.NewBatchPerFile;

    /// <summary>See WatchSettings.DuplicateImportBehavior.</summary>
    [ObservableProperty]
    private DuplicateImportBehavior _duplicateImportBehavior = DuplicateImportBehavior.ImportAnyway;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private bool _checkForUpdatesOnStartup;

    [ObservableProperty]
    private bool _allowFieldScripts;

    public string DebugLogPath => _paths.DebugLogPath;

    [RelayCommand]
    private void OpenDebugLogFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(DebugLogPath) ?? _paths.Root;
            Directory.CreateDirectory(folder);
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{folder}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{folder}\"");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the folder: {ex.Message}";
            StatusIsError = true;
        }
    }

    public ObservableCollection<WatchFolderEntryViewModel> WatchFolders { get; } = [];

    [ObservableProperty]
    private string _aiEndpoint = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    [ObservableProperty]
    private string _aiModel = "gpt-4o-mini";

    [ObservableProperty]
    private int _aiMaxDocumentChars = AiExtractPrompt.MaxDocumentChars;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiOpenAiCompatible))]
    [NotifyPropertyChangedFor(nameof(IsAiLocal))]
    [NotifyPropertyChangedFor(nameof(IsAiNone))]
    private AiProvider _aiProvider = AiProvider.OpenAiCompatible;

    public IReadOnlyList<AiProvider> AiProviderOptions { get; } = Enum.GetValues<AiProvider>();

    public bool IsAiOpenAiCompatible => AiProvider == AiProvider.OpenAiCompatible;

    public bool IsAiLocal => AiProvider == AiProvider.Local;

    public bool IsAiNone => AiProvider == AiProvider.None;

    [ObservableProperty]
    private int _localAiMaxDocumentChars = 12_000;

    [ObservableProperty]
    private string _localAiModelStatus = "Not downloaded";

    [ObservableProperty]
    private bool _isDownloadingLocalAiModel;

    [ObservableProperty]
    private double _localAiDownloadProgress;

    public string LocalAiModelFileName => _localAiModelDownloader.ModelFileName;

    private void RefreshLocalAiModelStatus()
    {
        LocalAiModelStatus = File.Exists(_paths.LocalAiModelPath)
            ? $"Downloaded ({new FileInfo(_paths.LocalAiModelPath).Length / 1_000_000_000.0:0.0} GB)"
            : "Not downloaded";
    }

    [RelayCommand]
    private async Task DownloadLocalAiModelAsync()
    {
        IsDownloadingLocalAiModel = true;
        LocalAiDownloadProgress = 0;
        StatusText = $"Downloading {LocalAiModelFileName}…";
        StatusIsError = false;
        try
        {
            await _localAiModelDownloader.DownloadAsync(progress => LocalAiDownloadProgress = progress)
                .ConfigureAwait(true);
            StatusText = "Local AI model downloaded.";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Model download failed: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsDownloadingLocalAiModel = false;
            RefreshLocalAiModelStatus();
        }
    }

    [RelayCommand]
    private void OpenLocalAiModelFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LocalAiModelsDirectory);
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{_paths.LocalAiModelsDirectory}\"")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{_paths.LocalAiModelsDirectory}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{_paths.LocalAiModelsDirectory}\"");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the folder: {ex.Message}";
            StatusIsError = true;
        }
    }

    // --- Therefore ------------------------------------------------------------------------------

    // Tracks whether ThereforeBaseUrl currently holds a value we derived from the tenant name (true)
    // versus something the user typed themselves (false) — starts true since an empty URL is fair
    // game to auto-fill. Without this, auto-fill would only ever apply on the tenant field's first
    // keystroke: that keystroke fills in the URL, which then reads as "already has a value" and blocks
    // every subsequent keystroke from updating it.
    private bool _thereforeBaseUrlIsAutoFilled = true;
    private bool _settingThereforeBaseUrlProgrammatically;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThereforeConnectionDisplay))]
    private string _thereforeBaseUrl = string.Empty;

    partial void OnThereforeBaseUrlChanged(string value)
    {
        if (!_settingThereforeBaseUrlProgrammatically)
            _thereforeBaseUrlIsAutoFilled = string.IsNullOrWhiteSpace(value);
    }

    [ObservableProperty]
    private string _thereforeTenantName = string.Empty;

    partial void OnThereforeTenantNameChanged(string value)
    {
        // Prefill convenience only — never clobbers a manually-typed on-prem URL.
        if (!_thereforeBaseUrlIsAutoFilled || string.IsNullOrWhiteSpace(value))
            return;

        _settingThereforeBaseUrlProgrammatically = true;
        ThereforeBaseUrl = $"https://{value.Trim()}.thereforeonline.com";
        _settingThereforeBaseUrlProgrammatically = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThereforeBasic))]
    [NotifyPropertyChangedFor(nameof(IsThereforeBearer))]
    private Core.Watch.ThereforeAuthMethod _thereforeAuthMethod = Core.Watch.ThereforeAuthMethod.Basic;

    public IReadOnlyList<Core.Watch.ThereforeAuthMethod> ThereforeAuthMethodOptions { get; } = Enum.GetValues<Core.Watch.ThereforeAuthMethod>();

    public bool IsThereforeBasic => ThereforeAuthMethod == Core.Watch.ThereforeAuthMethod.Basic;

    public bool IsThereforeBearer => ThereforeAuthMethod == Core.Watch.ThereforeAuthMethod.Bearer;

    [ObservableProperty]
    private string _thereforeUsername = string.Empty;

    [ObservableProperty]
    private string _thereforePassword = string.Empty;

    [ObservableProperty]
    private string _thereforeBearerToken = string.Empty;

    public string ThereforeConnectionDisplay =>
        string.IsNullOrWhiteSpace(ThereforeBaseUrl) ? "Not configured" : ThereforeBaseUrl;

    [RelayCommand]
    private async Task TestThereforeConnectionAsync()
    {
        var connection = new ThereforeConnectionSettings
        {
            BaseUrl = ThereforeBaseUrl,
            TenantName = string.IsNullOrWhiteSpace(ThereforeTenantName) ? null : ThereforeTenantName.Trim(),
            AuthMethod = ThereforeAuthMethod == Core.Watch.ThereforeAuthMethod.Bearer
                ? global::Capture.Therefore.ThereforeAuthMethod.Bearer
                : global::Capture.Therefore.ThereforeAuthMethod.Basic,
            Username = ThereforeUsername,
            Password = ThereforePassword,
            BearerToken = ThereforeBearerToken
        };

        StatusText = "Testing Therefore connection…";
        StatusIsError = false;
        try
        {
            var ok = await _thereforeClient.TestConnectionAsync(connection).ConfigureAwait(true);
            StatusText = ok ? "Therefore connection succeeded" : "Therefore connection failed — no token returned";
            StatusIsError = !ok;
            if (ok) _toasts.ShowSuccess(StatusText); else _toasts.ShowError(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Therefore connection failed: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    public void AttachHost(object host) => _dialogs.Host = host;

    public async Task InitializeAsync()
    {
        Profiles.Clear();
        foreach (var profile in await _profiles.GetAllAsync())
            Profiles.Add(profile);

        ImportProfiles.Clear();
        foreach (var importProfile in await _importProfiles.GetAllAsync())
            ImportProfiles.Add(importProfile);

        var settings = await _store.LoadAsync();
        StartView = settings.StartView;
        Theme = settings.Theme;
        NoBatchProfileBehavior = settings.NoBatchProfileBehavior;
        DuplicateImportBehavior = settings.DuplicateImportBehavior;
        AutoDeleteExportedDocuments = settings.AutoDeleteExportedDocuments;
        CleanupOlderThanDays = settings.AutoDeleteExportedDocumentsAfterDays;
        RemoveDocumentsAfterExport = settings.RemoveDocumentsAfterExport;
        TrashRetentionDays = settings.TrashRetentionDays;
        DebugMode = settings.DebugMode;
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        AllowFieldScripts = settings.AllowFieldScripts;

        WatchFolders.Clear();
        foreach (var entry in settings.WatchFolders)
            WatchFolders.Add(WrapEntry(entry));

        AiEndpoint = settings.AiEndpoint ?? "https://api.openai.com/v1";
        AiApiKey = settings.AiApiKey ?? string.Empty;
        AiModel = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel;
        AiMaxDocumentChars = settings.AiMaxDocumentChars > 0 ? settings.AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars;
        AiProvider = settings.AiProvider;
        LocalAiMaxDocumentChars = settings.LocalAiMaxDocumentChars > 0 ? settings.LocalAiMaxDocumentChars : 12_000;
        RefreshLocalAiModelStatus();

        // Set BaseUrl before TenantName so the tenant-changed prefill (which only fires when the URL
        // is still blank) never overwrites an already-saved URL.
        ThereforeBaseUrl = settings.ThereforeBaseUrl ?? string.Empty;
        ThereforeTenantName = settings.ThereforeTenantName ?? string.Empty;
        ThereforeAuthMethod = settings.ThereforeAuthMethod;
        ThereforeUsername = settings.ThereforeUsername ?? string.Empty;
        ThereforePassword = settings.ThereforePassword ?? string.Empty;
        ThereforeBearerToken = settings.ThereforeBearerToken ?? string.Empty;

        ScanDpi = settings.ScanDpi > 0 ? settings.ScanDpi : 200;
        ScanGrayscale = settings.ScanGrayscale;
        SelectedScanSource = settings.ScanSource == ScanInputSource.Feeder ? ScanSourceKind.Feeder : ScanSourceKind.Flatbed;
        ScanDuplex = settings.ScanDuplex;
        _pendingScanDeviceId = settings.ScanPreferredDeviceId;

        await LoadRedactionSetsAsync();

        // The device's capability probe (native helper subprocess + ImageCaptureCore) can be slow,
        // incomplete, or time out to restrictive fallback values — see RefreshScanCapabilities. None
        // of that should be allowed to silently overwrite the DPI/source/duplex the user explicitly
        // saved just because this one probe didn't confirm them.
        _restoringSettings = true;
        try
        {
            await RefreshScanDevicesAsync();
        }
        finally
        {
            _restoringSettings = false;
        }
    }

    // See InitializeAsync's call to RefreshScanDevicesAsync.
    private bool _restoringSettings;

    // --- Scanning -----------------------------------------------------------------------------------

    public bool IsScanningAvailable => _scanSource.IsAvailable;

    [ObservableProperty]
    private int _scanDpi = 200;

    [ObservableProperty]
    private bool _scanGrayscale;

    [ObservableProperty]
    private ScanSourceKind _selectedScanSource;

    [ObservableProperty]
    private bool _scanDuplex;

    public ObservableCollection<int> ScanDpiOptions { get; } = [];

    public ObservableCollection<ScanSourceKind> ScanSourceOptions { get; } = [];

    public bool CanScanDuplex => SelectedScanDevice?.SupportsDuplex == true && SelectedScanSource == ScanSourceKind.Feeder;

    public bool CanScanGrayscale => SelectedScanDevice?.SupportsGrayscale != false;

    public ObservableCollection<ScanDevice> ScanDevices { get; } = [];

    [ObservableProperty]
    private ScanDevice? _selectedScanDevice;

    // Holds the saved preferred-device Id until the device list has actually been loaded (it's an
    // async call, so on first InitializeAsync SelectedScanDevice can't be resolved from it yet).
    private string? _pendingScanDeviceId;

    [RelayCommand]
    private async Task RefreshScanDevicesAsync()
    {
        if (!_scanSource.IsAvailable)
            return;

        var previousId = SelectedScanDevice?.Id ?? _pendingScanDeviceId;
        ScanDevices.Clear();
        try
        {
            foreach (var device in await _scanSource.ListDevicesAsync())
                ScanDevices.Add(device);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not list scanners: {ex.Message}";
            StatusIsError = true;
            return;
        }

        SelectedScanDevice = ScanDevices.FirstOrDefault(device => device.Id == previousId) ?? ScanDevices.FirstOrDefault();
        _pendingScanDeviceId = null;
    }

    partial void OnSelectedScanDeviceChanged(ScanDevice? value) => RefreshScanCapabilities(value);

    partial void OnSelectedScanSourceChanged(ScanSourceKind value)
    {
        if (value != ScanSourceKind.Feeder)
            ScanDuplex = false;
        OnPropertyChanged(nameof(CanScanDuplex));
    }

    // While restoring saved settings (see InitializeAsync), a device's capability probe is trusted
    // to EXPAND what's shown as available, but never to silently overturn what the user explicitly
    // saved — the probe (a native helper subprocess + ImageCaptureCore) can be slow, report an
    // incomplete capability set, or fall back to restrictive defaults on timeout, and none of that
    // is a reason to forget the user's choice. Once the user actively changes device/source in this
    // window, corrective clamping to the confirmed capabilities resumes as normal.
    private void RefreshScanCapabilities(ScanDevice? device)
    {
        var previousDpi = ScanDpi;
        ScanDpiOptions.Clear();
        foreach (var dpi in (device?.SupportedDpis is { Count: > 0 } supported
                     ? supported
                     : new[] { 75, 100, 150, 200, 300, 400, 600, 1200 })
                 .Where(dpi => dpi > 0)
                 .Distinct()
                 .Order())
            ScanDpiOptions.Add(dpi);

        if (_restoringSettings)
        {
            if (previousDpi > 0 && !ScanDpiOptions.Contains(previousDpi))
                InsertSorted(ScanDpiOptions, previousDpi);
        }
        else if (ScanDpiOptions.Count > 0 && !ScanDpiOptions.Contains(previousDpi))
        {
            ScanDpi = ScanDpiOptions.MinBy(dpi => Math.Abs(dpi - previousDpi));
        }

        ScanSourceOptions.Clear();
        if (device?.SupportsFlatbed != false)
            ScanSourceOptions.Add(ScanSourceKind.Flatbed);
        if (device?.SupportsFeeder == true)
            ScanSourceOptions.Add(ScanSourceKind.Feeder);

        if (_restoringSettings)
        {
            if (!ScanSourceOptions.Contains(SelectedScanSource))
                ScanSourceOptions.Add(SelectedScanSource);
        }
        else if (!ScanSourceOptions.Contains(SelectedScanSource))
        {
            SelectedScanSource = ScanSourceOptions.FirstOrDefault();
        }

        if (!_restoringSettings)
        {
            if (device?.SupportsGrayscale == false)
                ScanGrayscale = false;
            if (!CanScanDuplex)
                ScanDuplex = false;
        }
        OnPropertyChanged(nameof(CanScanDuplex));
        OnPropertyChanged(nameof(CanScanGrayscale));

        // ScanDpiOptions/ScanSourceOptions are rebuilt above by Clear()-then-Add(), but ScanDpi/
        // SelectedScanSource themselves may not have changed value at all (the common case: the
        // saved DPI/source were already valid, so neither branch above reassigns them). The bound
        // ComboBoxes' SelectedItem only resolves against an ItemsSource snapshot taken when they were
        // first attached — which, in the InitializeAsync path, happens with an EMPTY collection an
        // instant before this method fills it in, so with no value-change to notify on, the box is
        // left showing nothing even though ScanDpi/SelectedScanSource are already correct. Explicitly
        // re-raising the change notification (even for a same-value "change") forces the ComboBox to
        // re-resolve SelectedItem against the now-populated list instead of leaving a stale/blank
        // display until the user manually reselects something.
        OnPropertyChanged(nameof(ScanDpi));
        OnPropertyChanged(nameof(SelectedScanSource));
    }

    private static void InsertSorted(ObservableCollection<int> options, int value)
    {
        var index = 0;
        while (index < options.Count && options[index] < value)
            index++;
        options.Insert(index, value);
    }

    // --- Redaction sets ---------------------------------------------------------------------------

    public ObservableCollection<RedactionEntitySetRow> RedactionSets { get; } = [];

    [ObservableProperty]
    private bool _isEditingRedactionSet;

    private Guid? _editingRedactionSetId;

    [ObservableProperty]
    private string _redactionSetName = string.Empty;

    /// <summary>The grouped checklist backing the add/edit form — rebuilt fresh each time the editor
    /// opens, from Capture.Core.Redaction.PresidioEntityNames.Groups.</summary>
    public ObservableCollection<RedactionEntityGroupRow> RedactionSetGroups { get; } = [];

    private async Task LoadRedactionSetsAsync()
    {
        RedactionSets.Clear();
        foreach (var set in BuiltInRedactionSets.All)
            RedactionSets.Add(new RedactionEntitySetRow(set, isBuiltIn: true));
        foreach (var set in await _redactionEntitySets.GetAllAsync())
            RedactionSets.Add(new RedactionEntitySetRow(set, isBuiltIn: false));
    }

    [RelayCommand]
    private void NewRedactionSet()
    {
        _editingRedactionSetId = null;
        RedactionSetName = string.Empty;
        BuildRedactionSetGroups([]);
        IsEditingRedactionSet = true;
    }

    [RelayCommand]
    private void EditRedactionSet(RedactionEntitySetRow row)
    {
        if (row.IsBuiltIn)
            return;

        _editingRedactionSetId = row.Id;
        RedactionSetName = row.Name;
        BuildRedactionSetGroups(row.Set.Entities);
        IsEditingRedactionSet = true;
    }

    private void BuildRedactionSetGroups(IEnumerable<string> selectedEntities)
    {
        var selected = selectedEntities.ToHashSet();
        RedactionSetGroups.Clear();
        foreach (var group in PresidioEntityNames.Groups)
            RedactionSetGroups.Add(new RedactionEntityGroupRow(group.Name, group.Entities, selected));
    }

    [RelayCommand]
    private void CancelRedactionSetEdit() => IsEditingRedactionSet = false;

    [RelayCommand]
    private async Task SaveRedactionSetAsync()
    {
        var name = RedactionSetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = "Name the redaction set before saving";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
            return;
        }

        var set = new RedactionEntitySet
        {
            Id = _editingRedactionSetId ?? Guid.NewGuid(),
            Name = name,
            Entities = RedactionSetGroups.SelectMany(group => group.SelectedEntities).ToList()
        };
        await _redactionEntitySets.SaveAsync(set);
        IsEditingRedactionSet = false;
        await LoadRedactionSetsAsync();
        StatusText = $"Saved redaction set \"{name}\"";
        StatusIsError = false;
        _toasts.ShowSuccess(StatusText);
    }

    [RelayCommand]
    private async Task DeleteRedactionSetAsync(RedactionEntitySetRow row)
    {
        if (row.IsBuiltIn)
            return;

        await _redactionEntitySets.DeleteAsync(row.Id);
        await LoadRedactionSetsAsync();
    }

    private WatchFolderEntryViewModel WrapEntry(WatchFolderEntry entry)
    {
        var row = new WatchFolderEntryViewModel(entry)
        {
            SelectedProfile = entry.ProfileId is { } id ? Profiles.FirstOrDefault(profile => profile.Id == id) : null,
            SelectedImportProfile = entry.ImportProfileId is { } importId
                ? ImportProfiles.FirstOrDefault(profile => profile.Id == importId)
                : null,
            BrowseRequested = OnBrowseWatchFolder,
            RemoveRequested = OnRemoveWatchFolder
        };
        return row;
    }

    [RelayCommand]
    private void AddWatchFolder() => WatchFolders.Add(WrapEntry(new WatchFolderEntry { Enabled = true }));

    private async void OnBrowseWatchFolder(WatchFolderEntryViewModel row)
    {
        var folder = await _dialogs.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
            row.Folder = folder;
    }

    private void OnRemoveWatchFolder(WatchFolderEntryViewModel row) => WatchFolders.Remove(row);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryBuildSettings(out var settings))
            return;

        await _store.SaveAsync(settings);
        Saved = true;
        Close?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke();

    private bool TryBuildSettings(out WatchSettings settings)
    {
        settings = new WatchSettings();
        var enabled = WatchFolders.Where(row => row.Enabled).ToList();
        foreach (var row in enabled)
        {
            if (string.IsNullOrWhiteSpace(row.Folder) || !Directory.Exists(row.Folder))
            {
                StatusText = string.IsNullOrWhiteSpace(row.Folder)
                    ? "Choose a folder for every enabled watch entry"
                    : $"\"{row.Folder}\" doesn't exist";
                StatusIsError = true;
                _toasts.ShowError(StatusText);
                return false;
            }
        }

        var duplicate = enabled
            .GroupBy(row => Path.GetFullPath(row.Folder), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            StatusText = $"\"{duplicate.Key}\" is watched by more than one entry";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
            return false;
        }

        settings = new WatchSettings
        {
            StartView = StartView,
            Theme = Theme,
            NoBatchProfileBehavior = NoBatchProfileBehavior,
            DuplicateImportBehavior = DuplicateImportBehavior,
            AutoDeleteExportedDocuments = AutoDeleteExportedDocuments,
            AutoDeleteExportedDocumentsAfterDays = Math.Max(1, CleanupOlderThanDays),
            RemoveDocumentsAfterExport = RemoveDocumentsAfterExport,
            TrashRetentionDays = Math.Max(1, TrashRetentionDays),
            DebugMode = DebugMode,
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
            AllowFieldScripts = AllowFieldScripts,
            WatchFolders = WatchFolders.Select(row => row.ToModel()).ToList(),
            AiEndpoint = string.IsNullOrWhiteSpace(AiEndpoint) ? null : AiEndpoint.Trim(),
            AiApiKey = string.IsNullOrWhiteSpace(AiApiKey) ? null : AiApiKey.Trim(),
            AiModel = string.IsNullOrWhiteSpace(AiModel) ? "gpt-4o-mini" : AiModel.Trim(),
            AiMaxDocumentChars = AiMaxDocumentChars > 0 ? AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars,
            AiProvider = AiProvider,
            LocalAiMaxDocumentChars = LocalAiMaxDocumentChars > 0 ? LocalAiMaxDocumentChars : 12_000,
            ThereforeBaseUrl = string.IsNullOrWhiteSpace(ThereforeBaseUrl) ? null : ThereforeBaseUrl.Trim(),
            ThereforeTenantName = string.IsNullOrWhiteSpace(ThereforeTenantName) ? null : ThereforeTenantName.Trim(),
            ThereforeAuthMethod = ThereforeAuthMethod,
            ThereforeUsername = string.IsNullOrWhiteSpace(ThereforeUsername) ? null : ThereforeUsername.Trim(),
            ThereforePassword = string.IsNullOrWhiteSpace(ThereforePassword) ? null : ThereforePassword,
            ThereforeBearerToken = string.IsNullOrWhiteSpace(ThereforeBearerToken) ? null : ThereforeBearerToken,
            ScanDpi = ScanDpi > 0 ? ScanDpi : 200,
            ScanGrayscale = ScanGrayscale,
            ScanSource = SelectedScanSource == ScanSourceKind.Feeder ? ScanInputSource.Feeder : ScanInputSource.Flatbed,
            ScanDuplex = ScanDuplex,
            ScanPreferredDeviceId = SelectedScanDevice?.Id
        };
        return true;
    }

    /// <summary>Off by default — see ExportSettingsAsync. A settings export normally carries
    /// placeholders instead of the AI API key / Therefore password / Therefore bearer token, so a
    /// routine backup or "here's my config" share doesn't leak credentials by default.</summary>
    [ObservableProperty]
    private bool _includeCredentialsInExport;

    /// <summary>See WatchSettings.AutoDeleteExportedDocuments — persisted, so MainViewModel can act on
    /// it at startup/on save, distinct from the one-off "Clean up now" button below.</summary>
    [ObservableProperty]
    private bool _autoDeleteExportedDocuments;

    [ObservableProperty]
    private int _cleanupOlderThanDays = 30;

    /// <summary>See WatchSettings.RemoveDocumentsAfterExport — immediate, workspace-wide, distinct
    /// from the age-gated AutoDeleteExportedDocuments sweep above.</summary>
    [ObservableProperty]
    private bool _removeDocumentsAfterExport;

    /// <summary>See WatchSettings.TrashRetentionDays.</summary>
    [ObservableProperty]
    private int _trashRetentionDays = 30;

    /// <summary>Deletes every already-exported document immediately, regardless of age — deliberately
    /// never touches anything still needing attention (NeedsReview, Ready-but-not-yet-exported, Error,
    /// Processing/Queued), since this runs against documents the reviewer isn't looking at one by one,
    /// unlike the main window's own Remove action. Unlike AutoDeleteExportedDocuments/
    /// CleanupOlderThanDays (age-gated, automatic), this is an immediate, unconditional sweep the
    /// reviewer explicitly asks for.</summary>
    [RelayCommand]
    private async Task CleanUpOldDocumentsAsync()
    {
        var exported = DocumentCleanup.SelectExported(await _documents.GetAllAsync());

        if (exported.Count == 0)
        {
            StatusText = "No exported documents to clean up";
            _toasts.ShowSuccess(StatusText);
            return;
        }

        if (_dialogs.Host is not { } host)
            return;

        var confirmed = await _confirm.ConfirmAsync(
            host,
            "Delete all exported documents?",
            $"This moves all {exported.Count} exported document(s) to Trash, regardless of how recent they are. Documents still needing review, or not yet exported, are never included. Restore them from Trash (Table mode) any time before the retention period passes.",
            confirmText: "Delete",
            cancelText: "Cancel");
        if (!confirmed)
            return;

        foreach (var document in exported)
            await _documents.SoftDeleteAsync(document.Id);

        DocumentsChanged = true;
        StatusText = $"Deleted {exported.Count} document(s)";
        _toasts.ShowSuccess(StatusText);
    }

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        if (!TryBuildSettings(out var settings))
            return;

        if (IncludeCredentialsInExport)
        {
            var confirmed = _dialogs.Host is not { } host
                || await _confirm.ConfirmAsync(
                    host,
                    "Export credentials in plain text?",
                    "This file will contain your AI API key and/or Therefore password/bearer token, unencrypted. Anyone who gets this file can use them. Only proceed if you're sending it somewhere you trust (e.g. your own backup), not a general config share.",
                    confirmText: "Include credentials",
                    cancelText: "Cancel");
            if (!confirmed)
                return;
        }
        else
        {
            settings.AiApiKey = CredentialRedaction.Redact(settings.AiApiKey);
            settings.ThereforePassword = CredentialRedaction.Redact(settings.ThereforePassword);
            settings.ThereforeBearerToken = CredentialRedaction.Redact(settings.ThereforeBearerToken);
        }

        var path = await _dialogs.PickSaveJsonFileAsync("Export settings", "capture-settings.json");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, settings, CaptureJsonOptions.Default);
            StatusText = $"Exported settings to {path}";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        var path = await _dialogs.PickJsonFileAsync("Import settings");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<WatchSettings>(stream, CaptureJsonOptions.Default);
            if (settings is null)
            {
                StatusText = "That file doesn't contain valid settings";
                StatusIsError = true;
                _toasts.ShowError(StatusText);
                return;
            }

            StartView = settings.StartView;
            Theme = settings.Theme;
            NoBatchProfileBehavior = settings.NoBatchProfileBehavior;
            DuplicateImportBehavior = settings.DuplicateImportBehavior;
            AutoDeleteExportedDocuments = settings.AutoDeleteExportedDocuments;
            CleanupOlderThanDays = settings.AutoDeleteExportedDocumentsAfterDays;
            RemoveDocumentsAfterExport = settings.RemoveDocumentsAfterExport;
            TrashRetentionDays = settings.TrashRetentionDays;
            DebugMode = settings.DebugMode;
            CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
            AllowFieldScripts = settings.AllowFieldScripts;

            WatchFolders.Clear();
            foreach (var entry in settings.WatchFolders)
                WatchFolders.Add(WrapEntry(entry));

            AiEndpoint = settings.AiEndpoint ?? "https://api.openai.com/v1";
            AiApiKey = CredentialRedaction.PreserveIfRedacted(settings.AiApiKey, AiApiKey);
            AiModel = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel;
            AiMaxDocumentChars = settings.AiMaxDocumentChars > 0 ? settings.AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars;
            AiProvider = settings.AiProvider;
            LocalAiMaxDocumentChars = settings.LocalAiMaxDocumentChars > 0 ? settings.LocalAiMaxDocumentChars : 12_000;

            ThereforeBaseUrl = settings.ThereforeBaseUrl ?? string.Empty;
            ThereforeTenantName = settings.ThereforeTenantName ?? string.Empty;
            ThereforeAuthMethod = settings.ThereforeAuthMethod;
            ThereforeUsername = settings.ThereforeUsername ?? string.Empty;
            ThereforePassword = CredentialRedaction.PreserveIfRedacted(settings.ThereforePassword, ThereforePassword);
            ThereforeBearerToken = CredentialRedaction.PreserveIfRedacted(settings.ThereforeBearerToken, ThereforeBearerToken);

            ScanGrayscale = settings.ScanGrayscale;
            SelectedScanSource = settings.ScanSource == ScanInputSource.Feeder ? ScanSourceKind.Feeder : ScanSourceKind.Flatbed;
            ScanDuplex = settings.ScanDuplex;
            ScanDpi = settings.ScanDpi > 0 ? settings.ScanDpi : 200;
            SelectedScanDevice = ScanDevices.FirstOrDefault(device => device.Id == settings.ScanPreferredDeviceId)
                ?? SelectedScanDevice;

            StatusText = "Settings imported — review and click Save to apply";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (JsonException)
        {
            StatusText = "That file doesn't contain valid settings";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
    }

    [RelayCommand]
    private void OpenCatalogFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(_paths.AiFieldCatalogPath) ?? _paths.Root;
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{folder}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{folder}\"");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the folder: {ex.Message}";
            StatusIsError = true;
        }
    }

    [RelayCommand]
    private async Task ReloadCatalogAsync()
    {
        var types = await _catalogStore.LoadAsync();
        AiFieldCatalog.Load(types);
        StatusText = $"Reloaded {types.Count} AI field type(s) from {AiFieldCatalogPath}";
        StatusIsError = false;
        _toasts.ShowSuccess(StatusText);
    }
}
