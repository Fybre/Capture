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

    /// <summary>What the toolbar's Capture profile picker actually binds its ItemsSource to — the same
    /// enabled profiles as <see cref="CaptureProfiles"/>, with the "None" sentinel always first. Kept
    /// separate from <see cref="CaptureProfiles"/> so the sentinel never leaks into the id-based lookups
    /// elsewhere (ApplyWatchAsync's enabled-profile-id set, Import.cs's profile-by-id resolution) that use
    /// CaptureProfiles directly.</summary>
    public ObservableCollection<CaptureProfile> CaptureProfilePickerItems { get; } = [];

    /// <summary>All profiles regardless of Enabled state. Used to resolve settings for documents that
    /// were already captured under a profile that has since been disabled — disabling a profile stops it
    /// from being offered for new capture work, but must not break export/ready/redaction for documents
    /// already assigned to it.</summary>
    private readonly ObservableCollection<CaptureProfile> _allCaptureProfiles = [];

    // Rebuilt alongside _allCaptureProfiles (LoadProfilesAsync only — profile/type counts don't change
    // between loads) so FindDocumentType/FindCaptureProfileForDocumentType are O(1) instead of an
    // O(profiles x types) linear scan repeated once per document during a bulk Inbox load.
    private Dictionary<Guid, DocumentTypeDefinition> _documentTypesById = [];
    private Dictionary<Guid, CaptureProfile> _profileByDocumentTypeId = [];

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
            if (SelectedCaptureProfile is not { } profile || profile.Id == BuiltInCaptureProfiles.UnsortedId)
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

    /// <summary>The picker's actual bound selection — a plain <see cref="Guid"/> matched against each
    /// item's own Id via SelectedValueBinding, rather than binding SelectedItem to the CaptureProfile
    /// object directly. This is deliberate, not a style choice: CaptureProfilePickerItems is repopulated
    /// every time profiles reload (including at startup), and a reference-based SelectedItem binding
    /// doesn't survive that reliably when the items are freshly deserialized objects. A scalar id has no
    /// such failure mode: an unrecognized value simply matches no profile and is ignored below, rather
    /// than silently overwriting a real selection. Mirrors the same SelectedValue/SelectedValueBinding
    /// pattern already used for FallbackDocumentTypeId and the PageDisposition pickers elsewhere in the
    /// Capture Profile designer.
    ///
    /// "None" (<see cref="BuiltInCaptureProfiles.Unsorted"/>) is just another entry in
    /// <see cref="CaptureProfilePickerItems"/> with a fixed, permanent Id — not represented by a C# null
    /// anywhere in this lookup. That is what keeps this simple: None restores, persists, and matches
    /// exactly like a real profile, with no separate "was a preference ever saved" bookkeeping needed to
    /// tell "never chosen" apart from "explicitly chose None".</summary>
    public Guid SelectedCaptureProfileIdOrNone
    {
        get => SelectedCaptureProfile?.Id ?? BuiltInCaptureProfiles.UnsortedId;
        set
        {
            if (CaptureProfilePickerItems.FirstOrDefault(profile => profile.Id == value) is { } profile)
                SelectedCaptureProfile = profile;
            // Any other value (e.g. Guid.Empty from a transient ComboBox reset) matches nothing on
            // purpose — ignored, leaving whatever is currently selected untouched.
        }
    }

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
            RebuildDocumentTypeLookups(all);

            var enabledProfiles = all.Where(profile => profile.Enabled).ToList();
            // SyncFrom rather than Clear()+Add(): CaptureProfilePickerItems drives the toolbar
            // ComboBox's SelectedValue, and Clear() raises a Reset while the collection is momentarily
            // empty — a ComboBox reacting to that can end up displaying nothing even once the desired
            // items (always at least "None") are back in place. Never touching the collection unless an
            // index's value actually changed avoids that empty-list window entirely. See
            // ObservableCollectionSync's own doc comment for the general rationale.
            CaptureProfiles.SyncFrom(enabledProfiles);
            var pickerItems = new List<CaptureProfile>(enabledProfiles.Count + 1) { BuiltInCaptureProfiles.Unsorted };
            pickerItems.AddRange(enabledProfiles);
            CaptureProfilePickerItems.SyncFrom(pickerItems);

            // restoreId is null only when no preference has ever been saved (a fresh install) — None
            // itself always has a real, permanent Id here, so it never needs this fallback. Defaulting
            // the "nothing else to pick" case (zero enabled profiles) to Unsorted rather than null keeps
            // SelectedCaptureProfile a real, always-selectable value in every steady state.
            SelectedCaptureProfile = restoreId is { } id
                ? CaptureProfilePickerItems.FirstOrDefault(profile => profile.Id == id) ?? BuiltInCaptureProfiles.Unsorted
                : CaptureProfiles.FirstOrDefault() ?? BuiltInCaptureProfiles.Unsorted;
            // Unconditional: if this assignment happens to land on the exact same object
            // SelectedCaptureProfile already held, CommunityToolkit's own change detection skips
            // OnSelectedCaptureProfileChanged entirely, so the picker would otherwise never be told to
            // re-sync against the just-updated CaptureProfilePickerItems.
            OnPropertyChanged(nameof(SelectedCaptureProfileIdOrNone));

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
        OnPropertyChanged(nameof(SelectedCaptureProfileIdOrNone));
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
        var currentId = SelectedCaptureProfile?.Id;
        if (_watchSettings.LastCaptureProfileId == currentId)
            return;
        _watchSettings.LastCaptureProfileId = currentId;
        await _watchStore.SaveAsync(_watchSettings).ConfigureAwait(true);
    }

    // Searches every stored profile, not just the enabled ones offered for new capture work — a document
    // already captured under a profile must still resolve its type/settings after that profile is disabled.
    private DocumentTypeDefinition? FindDocumentType(Guid? id) =>
        id is { } value ? _documentTypesById.GetValueOrDefault(value) : null;

    private CaptureProfile? FindCaptureProfileForDocumentType(Guid? id) =>
        id is { } value ? _profileByDocumentTypeId.GetValueOrDefault(value) : null;

    // First-match-wins, matching the old FirstOrDefault-based lookups' semantics for the (invalid, but
    // not worth crashing over) case of a duplicate document-type id across profiles.
    private void RebuildDocumentTypeLookups(IReadOnlyList<CaptureProfile> profiles)
    {
        _documentTypesById = [];
        _profileByDocumentTypeId = [];
        foreach (var profile in profiles)
        {
            foreach (var type in profile.DocumentTypes)
            {
                _documentTypesById.TryAdd(type.Id, type);
                _profileByDocumentTypeId.TryAdd(type.Id, profile);
            }
        }
    }
}
