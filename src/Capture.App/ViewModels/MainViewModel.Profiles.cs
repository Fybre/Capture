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
            if (SelectedCaptureProfile is not { } profile) return string.Empty;
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
            CaptureProfiles.Clear();
            foreach (var profile in (await _captureProfileStore.GetAllAsync().ConfigureAwait(true)).Where(profile => profile.Enabled))
                CaptureProfiles.Add(profile);
            SelectedCaptureProfile = restoreId is { } id
                ? CaptureProfiles.FirstOrDefault(profile => profile.Id == id)
                : CaptureProfiles.FirstOrDefault();
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
        if (SelectedCaptureProfile is null || _store is not IOpenBatchStore batches) return;
        var open = await batches.GetOpenBatchAsync(SelectedCaptureProfile.Id, "manual").ConfigureAwait(true);
        if (open is not null)
        {
            await batches.SetBatchStateAsync(open.Id, BatchState.Closed).ConfigureAwait(true);
            StatusText = "Closed the current batch; the next manual import will start a new batch";
            _toasts.ShowSuccess(StatusText);
        }
        else
        {
            StatusText = "No batch is open; the next manual import will start a new batch";
        }
        await RefreshManualBatchStateAsync().ConfigureAwait(true);
    }

    private bool CanStartNewBatch() => !IsBusy && SelectedCaptureProfile is not null && HasOpenManualBatch && _store is IOpenBatchStore;

    private async Task RefreshManualBatchStateAsync()
    {
        var profile = SelectedCaptureProfile;
        if (profile is null || _store is not IOpenBatchStore batches)
        {
            HasOpenManualBatch = false;
            ManualBatchStatus = string.Empty;
            ManualBatchTooltip = string.Empty;
            return;
        }

        var open = await batches.GetOpenBatchAsync(profile.Id, "manual").ConfigureAwait(true);
        // Ignore a slower lookup for a profile the user has since changed away from.
        if (SelectedCaptureProfile?.Id != profile.Id) return;
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

    private DocumentTypeDefinition? FindDocumentType(Guid? id) => id is null
        ? null
        : CaptureProfiles.SelectMany(profile => profile.DocumentTypes).FirstOrDefault(type => type.Id == id);

    private CaptureProfile? FindCaptureProfileForDocumentType(Guid? id) => id is null
        ? null
        : CaptureProfiles.FirstOrDefault(profile => profile.DocumentTypes.Any(type => type.Id == id));
}
