using System.Collections.ObjectModel;
using System.Text.Json;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class BatchProfilesViewModel : ViewModelBase
{
    private readonly IBatchProfileStore _store;
    private readonly IProfileStore _profileStore;
    private readonly IProfileDialogService _profileDialog;
    private readonly IFileDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly ILatticeBuilder _latticeBuilder;
    private readonly IToastService _toasts;
    private readonly IBarcodeDecoder? _barcodes;

    public BatchProfilesViewModel(
        IBatchProfileStore store,
        IProfileStore profileStore,
        IProfileDialogService profileDialog,
        IFileDialogService dialogs,
        IAppPaths paths,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        ILatticeBuilder latticeBuilder,
        IToastService toasts,
        IBarcodeDecoder? barcodes = null)
    {
        _store = store;
        _profileStore = profileStore;
        _profileDialog = profileDialog;
        _dialogs = dialogs;
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _latticeBuilder = latticeBuilder;
        _toasts = toasts;
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
    [NotifyCanExecuteChangedFor(nameof(ExportProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportAllProfilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportProfileCommand))]
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

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile is null)
            return;

        var suggestedName = string.Join('_', SelectedProfile.Name.Split(Path.GetInvalidFileNameChars())) + ".json";
        var path = await _dialogs.PickSaveJsonFileAsync("Export batch profile", suggestedName);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, SelectedProfile, CaptureJsonOptions.Default);
            StatusText = $"Exported \"{SelectedProfile.Name}\" to {path}";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            _toasts.ShowError(StatusText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ExportAllProfilesAsync()
    {
        if (Profiles.Count == 0)
            return;

        var path = await _dialogs.PickSaveJsonFileAsync("Export all batch profiles", "batch-profiles.json");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, Profiles.ToList(), CaptureJsonOptions.Default);
            StatusText = $"Exported {Profiles.Count} batch profile(s) to {path}";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            _toasts.ShowError(StatusText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportProfileAsync()
    {
        var path = await _dialogs.PickJsonFileAsync("Import batch profile(s)");
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsBusy = true;
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream);
            // A file exported via "Export all" is a JSON array; a single-profile export is one object —
            // accept either so an "export all" file can be brought back in through the same button.
            var candidates = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                : new[] { document.RootElement }.AsEnumerable();

            var imported = new List<BatchProfile>();
            foreach (var element in candidates)
            {
                var profile = element.Deserialize<BatchProfile>(CaptureJsonOptions.Default);
                if (profile is null)
                    continue;

                // Always import as a new profile — reusing the file's own Id would silently overwrite
                // whatever profile on this machine happens to already have it.
                profile.Id = Guid.NewGuid();
                profile.CreatedUtc = DateTimeOffset.UtcNow;
                await _store.SaveAsync(profile);
                imported.Add(profile);
            }

            if (imported.Count == 0)
            {
                StatusText = "That file doesn't contain a valid batch profile";
                _toasts.ShowError(StatusText);
                return;
            }

            await ReloadAsync();
            SelectedProfile = Profiles.FirstOrDefault(item => item.Id == imported[^1].Id);
            StatusText = imported.Count == 1
                ? $"Imported \"{imported[0].Name}\""
                : $"Imported {imported.Count} batch profile(s)";
            _toasts.ShowSuccess(StatusText);
        }
        catch (JsonException)
        {
            StatusText = "That file doesn't contain a valid batch profile";
            _toasts.ShowError(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedProfileChanged(BatchProfile? value)
    {
        EditProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        ExportProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy && SelectedProfile is not null && !IsDesignerOpen;

    private bool CanImport() => !IsBusy;

    private async Task OpenDesignerAsync(BatchProfile profile, bool isNew)
    {
        var designer = new BatchProfileDesignerViewModel(
            profile, isNew, _store, _profileStore, _profileDialog, _dialogs, _paths, _pdfRasterizer,
            _imageImporter, _toasts, _barcodes, _latticeBuilder)
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
        StatusText = Profiles.Count == 0 ? "Create a batch profile to configure batch separation" : $"{Profiles.Count} batch profile(s)";
    }
}
