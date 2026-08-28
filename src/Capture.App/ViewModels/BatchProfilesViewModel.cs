using System.Collections.ObjectModel;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class BatchProfilesViewModel : ViewModelBase
{
    private readonly IBatchProfileStore _store;
    private readonly IFileDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly ILatticeBuilder _latticeBuilder;
    private readonly IBarcodeDecoder? _barcodes;

    public BatchProfilesViewModel(
        IBatchProfileStore store,
        IFileDialogService dialogs,
        IAppPaths paths,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        ILatticeBuilder latticeBuilder,
        IBarcodeDecoder? barcodes = null)
    {
        _store = store;
        _dialogs = dialogs;
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _latticeBuilder = latticeBuilder;
        _barcodes = barcodes;
    }

    public ObservableCollection<BatchProfile> Profiles { get; } = [];

    public bool HasNoProfiles => Profiles.Count == 0 && !IsDesignerOpen;

    [ObservableProperty]
    private BatchProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoProfiles))]
    private bool _isDesignerOpen;

    [ObservableProperty]
    private BatchProfileDesignerViewModel? _designer;

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
        var profile = new BatchProfile();
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
        await _store.DeleteAsync(id);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task CloseDesignerAsync()
    {
        Designer?.CleanupTestArtifacts();
        Designer = null;
        IsDesignerOpen = false;
        await ReloadAsync();
    }

    partial void OnSelectedProfileChanged(BatchProfile? value)
    {
        EditProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy && SelectedProfile is not null && !IsDesignerOpen;

    private Task OpenDesignerAsync(BatchProfile profile, bool isNew)
    {
        var designer = new BatchProfileDesignerViewModel(
            profile, isNew, _store, _dialogs, _paths, _pdfRasterizer, _imageImporter, _latticeBuilder, _barcodes)
        {
            CloseCommand = CloseDesignerCommand
        };
        Designer = designer;
        IsDesignerOpen = true;
        StatusText = string.Empty;
        return Task.CompletedTask;
    }

    private async Task ReloadAsync()
    {
        Profiles.Clear();
        foreach (var profile in await _store.GetAllAsync())
            Profiles.Add(profile);

        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoProfiles));
        StatusText = Profiles.Count == 0 ? "Create a batch profile to configure batch separation" : $"{Profiles.Count} batch profile(s)";
    }
}
