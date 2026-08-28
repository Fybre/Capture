using System.Collections.ObjectModel;
using System.Diagnostics;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Indexing;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Watch;
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
    private readonly IAppPaths _paths;

    public SettingsViewModel(
        IFileDialogService dialogs,
        IWatchSettingsStore store,
        IProfileStore profiles,
        IBatchProfileStore batchProfiles,
        IAiFieldCatalogStore catalogStore,
        IAppPaths paths)
    {
        _dialogs = dialogs;
        _store = store;
        _profiles = profiles;
        _batchProfiles = batchProfiles;
        _catalogStore = catalogStore;
        _paths = paths;
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

        WatchFolders.Clear();
        foreach (var entry in settings.WatchFolders)
            WatchFolders.Add(WrapEntry(entry));

        AiEndpoint = settings.AiEndpoint ?? "https://api.openai.com/v1";
        AiApiKey = settings.AiApiKey ?? string.Empty;
        AiModel = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel;
        AiMaxDocumentChars = settings.AiMaxDocumentChars > 0 ? settings.AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars;
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
        var enabled = WatchFolders.Where(row => row.Enabled).ToList();
        foreach (var row in enabled)
        {
            if (string.IsNullOrWhiteSpace(row.Folder) || !Directory.Exists(row.Folder))
            {
                StatusText = string.IsNullOrWhiteSpace(row.Folder)
                    ? "Choose a folder for every enabled watch entry"
                    : $"\"{row.Folder}\" doesn't exist";
                StatusIsError = true;
                return;
            }
        }

        var duplicate = enabled
            .GroupBy(row => Path.GetFullPath(row.Folder), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            StatusText = $"\"{duplicate.Key}\" is watched by more than one entry";
            StatusIsError = true;
            return;
        }

        await _store.SaveAsync(new WatchSettings
        {
            StartView = StartView,
            Theme = Theme,
            WatchFolders = WatchFolders.Select(row => row.ToModel()).ToList(),
            AiEndpoint = string.IsNullOrWhiteSpace(AiEndpoint) ? null : AiEndpoint.Trim(),
            AiApiKey = string.IsNullOrWhiteSpace(AiApiKey) ? null : AiApiKey.Trim(),
            AiModel = string.IsNullOrWhiteSpace(AiModel) ? "gpt-4o-mini" : AiModel.Trim(),
            AiMaxDocumentChars = AiMaxDocumentChars > 0 ? AiMaxDocumentChars : AiExtractPrompt.MaxDocumentChars
        });
        Saved = true;
        Close?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke();

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
