using System.Collections.ObjectModel;
using Capture.App.Services;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class ImportProfilesViewModel : ViewModelBase
{
    private readonly IImportProfileStore _store;
    private readonly IProfileStore _profileStore;
    private readonly IFileDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly IToastService _toasts;
    private readonly IBarcodeDecoder? _barcodes;

    public ImportProfilesViewModel(
        IImportProfileStore store,
        IProfileStore profileStore,
        IFileDialogService dialogs,
        IAppPaths paths,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        IToastService toasts,
        IBarcodeDecoder? barcodes = null)
    {
        _store = store;
        _profileStore = profileStore;
        _dialogs = dialogs;
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _toasts = toasts;
        _barcodes = barcodes;
    }

    public ObservableCollection<ImportProfile> Profiles { get; } = [];

    public bool HasNoProfiles => Profiles.Count == 0 && !IsDesignerOpen;

    [ObservableProperty]
    private ImportProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoProfiles))]
    private bool _isDesignerOpen;

    [ObservableProperty]
    private ImportProfileDesignerViewModel? _designer;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
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

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        var profile = new ImportProfile();
        await OpenDesignerAsync(profile, isNew: true);
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
        var name = SelectedProfile.Name;
        await _store.DeleteAsync(id);
        await ReloadAsync();
        _toasts.ShowSuccess($"Deleted \"{name}\"");
    }

    [RelayCommand]
    private async Task CloseDesignerAsync()
    {
        Designer?.Dispose();
        Designer = null;
        IsDesignerOpen = false;
        await ReloadAsync();
    }

    partial void OnSelectedProfileChanged(ImportProfile? value)
    {
        EditProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy && SelectedProfile is not null && !IsDesignerOpen;

    private async Task OpenDesignerAsync(ImportProfile profile, bool isNew)
    {
        var designer = new ImportProfileDesignerViewModel(
            profile, isNew, _store, _profileStore, _dialogs, _paths, _pdfRasterizer, _imageImporter, _toasts, _barcodes)
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
        StatusText = Profiles.Count == 0 ? "Create an import profile to configure document separation" : $"{Profiles.Count} import profile(s)";
    }
}
