using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Indexing;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Watch;
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
    private readonly IBatchProfileStore _batchProfiles;
    private readonly IAiFieldCatalogStore _catalogStore;
    private readonly IRedactionEntitySetStore _redactionEntitySets;
    private readonly IScanSource _scanSource;
    private readonly IAppPaths _paths;
    private readonly IThereforeClient _thereforeClient;

    public SettingsViewModel(
        IFileDialogService dialogs,
        IWatchSettingsStore store,
        IProfileStore profiles,
        IBatchProfileStore batchProfiles,
        IAiFieldCatalogStore catalogStore,
        IRedactionEntitySetStore redactionEntitySets,
        IScanSource scanSource,
        IAppPaths paths,
        IThereforeClient thereforeClient)
    {
        _dialogs = dialogs;
        _store = store;
        _profiles = profiles;
        _batchProfiles = batchProfiles;
        _catalogStore = catalogStore;
        _redactionEntitySets = redactionEntitySets;
        _scanSource = scanSource;
        _paths = paths;
        _thereforeClient = thereforeClient;
        WatchFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWatchFolders));
    }

    public bool HasWatchFolders => WatchFolders.Count > 0;

    public string AiFieldCatalogPath => _paths.AiFieldCatalogPath;

    public IReadOnlyList<WorkspaceMode> StartViewOptions { get; } = Enum.GetValues<WorkspaceMode>();

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = Enum.GetValues<AppTheme>();

    public ObservableCollection<IndexingProfile> Profiles { get; } = [];

    public ObservableCollection<BatchProfile> BatchProfiles { get; } = [];

    public bool Saved { get; private set; }

    public Action? Close { get; set; }

    [ObservableProperty]
    private WorkspaceMode _startView = WorkspaceMode.Preview;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private bool _debugMode;

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
        }
        catch (Exception ex)
        {
            StatusText = $"Therefore connection failed: {ex.Message}";
            StatusIsError = true;
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

        BatchProfiles.Clear();
        foreach (var batchProfile in await _batchProfiles.GetAllAsync())
            BatchProfiles.Add(batchProfile);

        var settings = await _store.LoadAsync();
        StartView = settings.StartView;
        Theme = settings.Theme;
        DebugMode = settings.DebugMode;

        WatchFolders.Clear();
        foreach (var entry in settings.WatchFolders)
            WatchFolders.Add(WrapEntry(entry));

        AiEndpoint = settings.AiEndpoint ?? "https://api.openai.com/v1";
        AiApiKey = settings.AiApiKey ?? string.Empty;
        AiModel = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel;
        AiMaxDocumentChars = settings.AiMaxDocumentChars > 0 ? settings.AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars;

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
        await RefreshScanDevicesAsync();
    }

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

        if (ScanDpiOptions.Count > 0 && !ScanDpiOptions.Contains(previousDpi))
            ScanDpi = ScanDpiOptions.MinBy(dpi => Math.Abs(dpi - previousDpi));

        ScanSourceOptions.Clear();
        if (device?.SupportsFlatbed != false)
            ScanSourceOptions.Add(ScanSourceKind.Flatbed);
        if (device?.SupportsFeeder == true)
            ScanSourceOptions.Add(ScanSourceKind.Feeder);
        if (!ScanSourceOptions.Contains(SelectedScanSource))
            SelectedScanSource = ScanSourceOptions.FirstOrDefault();

        if (device?.SupportsGrayscale == false)
            ScanGrayscale = false;
        if (!CanScanDuplex)
            ScanDuplex = false;
        OnPropertyChanged(nameof(CanScanDuplex));
        OnPropertyChanged(nameof(CanScanGrayscale));
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
            SelectedBatchProfile = entry.BatchProfileId is { } batchId
                ? BatchProfiles.FirstOrDefault(profile => profile.Id == batchId)
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
            return false;
        }

        settings = new WatchSettings
        {
            StartView = StartView,
            Theme = Theme,
            DebugMode = DebugMode,
            WatchFolders = WatchFolders.Select(row => row.ToModel()).ToList(),
            AiEndpoint = string.IsNullOrWhiteSpace(AiEndpoint) ? null : AiEndpoint.Trim(),
            AiApiKey = string.IsNullOrWhiteSpace(AiApiKey) ? null : AiApiKey.Trim(),
            AiModel = string.IsNullOrWhiteSpace(AiModel) ? "gpt-4o-mini" : AiModel.Trim(),
            AiMaxDocumentChars = AiMaxDocumentChars > 0 ? AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars,
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

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        if (!TryBuildSettings(out var settings))
            return;

        var path = await _dialogs.PickSaveJsonFileAsync("Export settings", "capture-settings.json");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, settings, CaptureJsonOptions.Default);
            StatusText = $"Exported settings to {path}";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            StatusIsError = true;
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
                return;
            }

            StartView = settings.StartView;
            Theme = settings.Theme;
            DebugMode = settings.DebugMode;

            WatchFolders.Clear();
            foreach (var entry in settings.WatchFolders)
                WatchFolders.Add(WrapEntry(entry));

            AiEndpoint = settings.AiEndpoint ?? "https://api.openai.com/v1";
            AiApiKey = settings.AiApiKey ?? string.Empty;
            AiModel = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel;
            AiMaxDocumentChars = settings.AiMaxDocumentChars > 0 ? settings.AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars;

            ThereforeBaseUrl = settings.ThereforeBaseUrl ?? string.Empty;
            ThereforeTenantName = settings.ThereforeTenantName ?? string.Empty;
            ThereforeAuthMethod = settings.ThereforeAuthMethod;
            ThereforeUsername = settings.ThereforeUsername ?? string.Empty;
            ThereforePassword = settings.ThereforePassword ?? string.Empty;
            ThereforeBearerToken = settings.ThereforeBearerToken ?? string.Empty;

            ScanGrayscale = settings.ScanGrayscale;
            SelectedScanSource = settings.ScanSource == ScanInputSource.Feeder ? ScanSourceKind.Feeder : ScanSourceKind.Flatbed;
            ScanDuplex = settings.ScanDuplex;
            ScanDpi = settings.ScanDpi > 0 ? settings.ScanDpi : 200;
            SelectedScanDevice = ScanDevices.FirstOrDefault(device => device.Id == settings.ScanPreferredDeviceId)
                ?? SelectedScanDevice;

            StatusText = "Settings imported — review and click Save to apply";
            StatusIsError = false;
        }
        catch (JsonException)
        {
            StatusText = "That file doesn't contain valid settings";
            StatusIsError = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            StatusIsError = true;
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
    }
}
