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

    public ObservableCollection<ImportProfile> ImportProfiles { get; } = [];

    /// <summary>Indexing profile chosen for manual (non-watch-folder) imports/applying — governs field
    /// extraction only. Named distinctly from <see cref="SelectedImportProfile"/> below (which governs
    /// document separation) since the two used to be the same, conflated concept.</summary>
    [ObservableProperty]
    private IndexingProfile? _selectedIndexingProfile;

    /// <summary>Import profile chosen for manual (non-watch-folder) imports — governs how an incoming
    /// file gets split into documents. Null means today's default (no splitting — everything appends
    /// into one document), same as before this concept existed.</summary>
    [ObservableProperty]
    private ImportProfile? _selectedImportProfile;

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
    private async Task OpenImportProfilesAsync()
    {
        var host = _dialogs.Host;
        if (host is null)
            return;
        await _importProfiles.ShowAsync(host);
        _dialogs.Host = host;
        await LoadImportProfilesAsync();
    }

    [RelayCommand]
    private void ClearIndexingProfile() => SelectedIndexingProfile = null;

    [RelayCommand]
    private void ClearImportProfile() => SelectedImportProfile = null;

    private async Task LoadProfilesAsync()
    {
        // Suppress the change-triggered persist below while restoring both selections from settings —
        // SelectedIndexingProfile is set here, and SelectedImportProfile a moment later inside
        // LoadImportProfilesAsync, each firing its own fire-and-forget PersistLastProfilesAsync. Without
        // this guard, an earlier call can race a later one and write a half-restored state to disk
        // before the last call corrects it.
        _restoringProfileSelection = true;
        try
        {
            var restoreId = SelectedIndexingProfile?.Id ?? _watchSettings.LastIndexingProfileId;
            Profiles.Clear();
            foreach (var profile in await _profileStore.GetAllAsync().ConfigureAwait(true))
                Profiles.Add(profile);

            SelectedIndexingProfile = restoreId is { } id
                ? Profiles.FirstOrDefault(profile => profile.Id == id)
                : null;

            await LoadBatchProfilesAsync().ConfigureAwait(true);
            await LoadImportProfilesAsync().ConfigureAwait(true);
        }
        finally
        {
            _restoringProfileSelection = false;
        }
    }

    // No longer toolbar-bound — batching is implied by whichever Import Profile is selected
    // (ImportProfile.BatchProfileId), not chosen independently. This collection still exists purely
    // to resolve that Id into a BatchProfile object (see ImportPathsAsync/ImportScannedPagesAsync)
    // and to populate the Import Profile Designer's own Batch Profile picker.
    private async Task LoadBatchProfilesAsync()
    {
        BatchProfiles.Clear();
        foreach (var profile in await _batchProfileStore.GetAllAsync().ConfigureAwait(true))
            BatchProfiles.Add(profile);
    }

    private async Task LoadImportProfilesAsync()
    {
        var restoreId = SelectedImportProfile?.Id ?? _watchSettings.LastImportProfileId;
        ImportProfiles.Clear();
        foreach (var profile in await _importProfileStore.GetAllAsync().ConfigureAwait(true))
            ImportProfiles.Add(profile);

        SelectedImportProfile = restoreId is { } id
            ? ImportProfiles.FirstOrDefault(profile => profile.Id == id)
            : null;
    }

    partial void OnSelectedIndexingProfileChanged(IndexingProfile? value)
    {
        if (!_restoringProfileSelection)
            _ = PersistLastProfilesAsync();
    }

    partial void OnSelectedImportProfileChanged(ImportProfile? value)
    {
        if (!_restoringProfileSelection)
            _ = PersistLastProfilesAsync();
    }

    private async Task PersistLastProfilesAsync()
    {
        if (_watchSettings.LastIndexingProfileId == SelectedIndexingProfile?.Id
            && _watchSettings.LastImportProfileId == SelectedImportProfile?.Id)
            return;

        _watchSettings.LastIndexingProfileId = SelectedIndexingProfile?.Id;
        _watchSettings.LastImportProfileId = SelectedImportProfile?.Id;
        await _watchStore.SaveAsync(_watchSettings).ConfigureAwait(true);
    }
}
