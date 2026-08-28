using System.Collections.ObjectModel;
using Capture.App.Services;
using Capture.Core.Indexing;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileStore _store;
    private readonly IProfileSampleService _samples;
    private readonly IFileDialogService _dialogs;
    private readonly IBarcodeDecoder _barcodes;
    private readonly IAiExtractor _ai;

    public ProfilesViewModel(
        IProfileStore store,
        IProfileSampleService samples,
        IFileDialogService dialogs,
        IBarcodeDecoder barcodes,
        IAiExtractor ai)
    {
        _store = store;
        _samples = samples;
        _dialogs = dialogs;
        _barcodes = barcodes;
        _ai = ai;
    }

    public ObservableCollection<IndexingProfile> Profiles { get; } = [];

    public bool HasNoProfiles => Profiles.Count == 0 && !IsDesignerOpen;

    [ObservableProperty]
    private IndexingProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoProfiles))]
    private bool _isDesignerOpen;

    [ObservableProperty]
    private ProfileDesignerViewModel? _designer;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private bool _isBusy;

    public void AttachHost(object host)
    {
        _dialogs.Host = host;
    }

    public async Task InitializeAsync()
    {
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task NewProfileAsync()
    {
        var path = await _dialogs.PickFileAsync("Choose a sample document");
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsBusy = true;
        try
        {
            var profile = new IndexingProfile
            {
                Name = Path.GetFileNameWithoutExtension(path)
            };
            StatusText = "Preparing sample…";
            await _samples.PrepareAsync(profile, path);
            await _store.SaveAsync(profile);
            await OpenDesignerAsync(profile, isNew: true);
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

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditProfileAsync()
    {
        if (SelectedProfile is null)
            return;
        await OpenDesignerAsync(SelectedProfile, isNew: false);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null)
            return;

        var id = SelectedProfile.Id;
        await _store.DeleteAsync(id);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task CloseDesignerAsync()
    {
        if (Designer is { IsNew: true, Saved: false } designer)
            await _store.DeleteAsync(designer.Profile.Id);

        Designer = null;
        IsDesignerOpen = false;
        await ReloadAsync();
    }

    partial void OnSelectedProfileChanged(IndexingProfile? value)
    {
        EditProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanMutate() => !IsBusy;

    private bool CanEdit() => !IsBusy && SelectedProfile is not null && !IsDesignerOpen;

    private async Task OpenDesignerAsync(IndexingProfile profile, bool isNew)
    {
        var designer = new ProfileDesignerViewModel(profile, isNew, _samples, _store, _barcodes, _ai)
        {
            CloseCommand = CloseDesignerCommand
        };
        Designer = designer;
        IsDesignerOpen = true;
        StatusText = string.Empty;
        await designer.InitializeAsync();
    }

    private async Task ReloadAsync()
    {
        Profiles.Clear();
        foreach (var profile in await _store.GetAllAsync())
            Profiles.Add(profile);

        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoProfiles));
        StatusText = Profiles.Count == 0 ? "Create a profile from a sample document" : $"{Profiles.Count} profile(s)";
    }
}
