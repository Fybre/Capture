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

public partial class MainViewModel
{
    private bool _restoringProfileSelection;

    public ObservableCollection<IndexingProfile> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public ObservableCollection<BatchProfile> BatchProfiles { get; } = [];

    /// <summary>Batch profile chosen for manual (non-watch-folder) imports — null means today's default,
    /// one new batch per import action.</summary>
    [ObservableProperty]
    private BatchProfile? _selectedBatchProfile;

    [ObservableProperty]
    private IndexingProfile? _selectedImportProfile;

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

    [RelayCommand]
    private void ClearBatchProfile() => SelectedBatchProfile = null;

    [RelayCommand]
    private void ClearImportProfile() => SelectedImportProfile = null;

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
}
