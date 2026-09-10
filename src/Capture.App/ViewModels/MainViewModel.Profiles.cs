using System.Collections.ObjectModel;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Models;
using Capture.Core.Store;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class MainViewModel
{
    private bool _restoringProfileSelection;

    public ObservableCollection<CaptureProfile> CaptureProfiles { get; } = [];
    public bool HasProfiles => CaptureProfiles.Count > 0;

    /// <summary>All profiles regardless of Enabled state. Used to resolve settings for documents that
    /// were already captured under a profile that has since been disabled — disabling a profile stops it
    /// from being offered for new capture work, but must not break export/ready/redaction for documents
    /// already assigned to it.</summary>
    private readonly ObservableCollection<CaptureProfile> _allCaptureProfiles = [];

    [ObservableProperty]
    private CaptureProfile? _selectedCaptureProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNewBatchCommand))]
    private bool _hasOpenManualBatch;

    [ObservableProperty]
    private string _manualBatchStatus = string.Empty;

    [ObservableProperty]
    private string _manualBatchTooltip = string.Empty;

    public string SelectedCaptureProfileSummary
    {
        get
        {
            if (SelectedCaptureProfile is not { } profile)
                return "No profile — ad hoc capture; documents get no fields and land under \"No profile applied\"";
            var batching = profile.Batch.StartNewBatchForEachFile
                ? "New batch for each input file"
                : profile.Batch.StartRules.MatchMode == SeparationMatchMode.None
                    ? "Manual imports continue the open batch"
                    : "Batch rules can start a new batch";
            var typeCount = profile.DocumentTypes.Count;
            var batchFieldCount = profile.Batch.Fields.Count;
            return $"{batching} · {typeCount} document type{(typeCount == 1 ? string.Empty : "s")}" +
                   $" · {batchFieldCount} batch index{(batchFieldCount == 1 ? string.Empty : "es")}" +
                   $" · Auto-export {(profile.AutoExportReadyDocuments ? "on" : "off")}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task OpenCaptureProfilesAsync()
    {
        var host = _dialogs.Host;
        if (host is null) return;
        await _captureProfiles.ShowAsync(host);
        _dialogs.Host = host;
        await LoadProfilesAsync();
        await ApplyWatchAsync();
    }

    [RelayCommand]
    private void ClearCaptureProfile() => SelectedCaptureProfile = null;

    private async Task LoadProfilesAsync()
    {
        _restoringProfileSelection = true;
        try
        {
            var restoreId = SelectedCaptureProfile?.Id ?? _watchSettings.LastCaptureProfileId;
            var previouslySelectedId = SelectedCaptureProfile?.Id;
            var all = await _captureProfileStore.GetAllAsync().ConfigureAwait(true);
            _allCaptureProfiles.Clear();
            foreach (var profile in all)
                _allCaptureProfiles.Add(profile);
            CaptureProfiles.Clear();
            foreach (var profile in all.Where(profile => profile.Enabled))
                CaptureProfiles.Add(profile);
            SelectedCaptureProfile = restoreId is { } id
                ? CaptureProfiles.FirstOrDefault(profile => profile.Id == id)
                : CaptureProfiles.FirstOrDefault();

            if (previouslySelectedId is { } previousId
                && SelectedCaptureProfile?.Id != previousId
                && _allCaptureProfiles.FirstOrDefault(profile => profile.Id == previousId) is { } previousProfile)
            {
                StatusText = SelectedCaptureProfile is { } newProfile
                    ? $"\"{previousProfile.Name}\" was disabled; switched the active profile to \"{newProfile.Name}\""
                    : $"\"{previousProfile.Name}\" was disabled; no other enabled profile is available";
                _toasts.ShowInfo(StatusText);
            }
        }
        finally { _restoringProfileSelection = false; }
    }

    partial void OnSelectedCaptureProfileChanged(CaptureProfile? value)
    {
        ImportFilesCommand.NotifyCanExecuteChanged();
        ImportFolderCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        StartNewBatchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedCaptureProfileSummary));
        _ = RefreshManualBatchStateAsync();
        if (!_restoringProfileSelection) _ = PersistLastProfileAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStartNewBatch))]
    private async Task StartNewBatchAsync()
    {
        if (_store is not IOpenBatchStore batches) return;
        var profile = SelectedCaptureProfile ?? BuiltInCaptureProfiles.Unsorted;
        var open = await batches.GetOpenBatchAsync(profile.Id, "manual").ConfigureAwait(true);
        if (open is not null)
        {
            await batches.SetBatchStateAsync(open.Id, BatchState.Closed).ConfigureAwait(true);
            StatusText = "Closed the current batch; the next manual import will start a new batch";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        else
        {
            StatusText = "No batch is open; the next manual import will start a new batch";
        }
        await RefreshManualBatchStateAsync().ConfigureAwait(true);
    }

    private bool CanStartNewBatch() => !IsBusy && HasOpenManualBatch && _store is IOpenBatchStore;

    private async Task RefreshManualBatchStateAsync()
    {
        var profile = SelectedCaptureProfile ?? BuiltInCaptureProfiles.Unsorted;
        if (_store is not IOpenBatchStore batches)
        {
            HasOpenManualBatch = false;
            ManualBatchStatus = string.Empty;
            ManualBatchTooltip = string.Empty;
            return;
        }

        var open = await batches.GetOpenBatchAsync(profile.Id, "manual").ConfigureAwait(true);
        // Ignore a slower lookup for a profile the user has since changed away from.
        var currentProfileId = (SelectedCaptureProfile ?? BuiltInCaptureProfiles.Unsorted).Id;
        if (currentProfileId != profile.Id) return;
        HasOpenManualBatch = open is not null;
        ManualBatchStatus = open is null
            ? "No manual batch is open — the next import starts one"
            : profile.Batch.StartNewBatchForEachFile
                ? "A batch is current — the next file starts a new batch automatically"
                : "A batch is open — new manual imports join it";
        ManualBatchTooltip = open is null ? string.Empty : $"Internal batch number: {open.Number}";
    }

    private async Task PersistLastProfileAsync()
    {
        if (_watchSettings.LastCaptureProfileId == SelectedCaptureProfile?.Id) return;
        _watchSettings.LastCaptureProfileId = SelectedCaptureProfile?.Id;
        await _watchStore.SaveAsync(_watchSettings).ConfigureAwait(true);
    }

    // Searches every stored profile, not just the enabled ones offered for new capture work — a document
    // already captured under a profile must still resolve its type/settings after that profile is disabled.
    private DocumentTypeDefinition? FindDocumentType(Guid? id) => id is null
        ? null
        : _allCaptureProfiles.SelectMany(profile => profile.DocumentTypes).FirstOrDefault(type => type.Id == id);

    private CaptureProfile? FindCaptureProfileForDocumentType(Guid? id) => id is null
        ? null
        : _allCaptureProfiles.FirstOrDefault(profile => profile.DocumentTypes.Any(type => type.Id == id));
}
