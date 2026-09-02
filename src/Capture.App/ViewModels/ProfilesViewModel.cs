using System.Collections.ObjectModel;
using System.Text.Json;
using Capture.App.Services;
using Capture.Core.Indexing;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.Storage;
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
    private readonly IRedactionEntitySetStore _redactionSets;
    private readonly IThereforeCategoryPickerDialogService _thereforeCategoryPicker;
    private readonly IToastService _toasts;
    private readonly IConfirmDialogService _confirm;
    private readonly IHelpWindowService _help;
    private readonly IFieldScriptRunner? _scriptRunner;

    public ProfilesViewModel(
        IProfileStore store,
        IProfileSampleService samples,
        IFileDialogService dialogs,
        IBarcodeDecoder barcodes,
        IAiExtractor ai,
        IRedactionEntitySetStore redactionSets,
        IThereforeCategoryPickerDialogService thereforeCategoryPicker,
        IToastService toasts,
        IConfirmDialogService confirm,
        IHelpWindowService help,
        IFieldScriptRunner? scriptRunner = null)
    {
        _store = store;
        _samples = samples;
        _dialogs = dialogs;
        _barcodes = barcodes;
        _ai = ai;
        _redactionSets = redactionSets;
        _thereforeCategoryPicker = thereforeCategoryPicker;
        _toasts = toasts;
        _confirm = confirm;
        _help = help;
        _scriptRunner = scriptRunner;
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
    [NotifyCanExecuteChangedFor(nameof(NewBlankProfileCommand))]
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
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // For a default/starter profile or a profile whose fields are entirely AI-extracted — no zones
    // ever get drawn against a sample, so there's no reason to require one up front.
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task NewBlankProfileAsync()
    {
        IsBusy = true;
        try
        {
            var profile = new IndexingProfile { Name = "New profile" };
            await _store.SaveAsync(profile);
            await OpenDesignerAsync(profile, isNew: true);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
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
        var name = SelectedProfile.Name;
        await _store.DeleteAsync(id);
        await ReloadAsync();
        _toasts.ShowSuccess($"Deleted \"{name}\"");
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile is null)
            return;

        var suggestedName = string.Join('_', SelectedProfile.Name.Split(Path.GetInvalidFileNameChars())) + ".json";
        var path = await _dialogs.PickSaveJsonFileAsync("Export indexing profile", suggestedName);
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

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task ExportAllProfilesAsync()
    {
        if (Profiles.Count == 0)
            return;

        var path = await _dialogs.PickSaveJsonFileAsync("Export all indexing profiles", "indexing-profiles.json");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, Profiles.ToList(), CaptureJsonOptions.Default);
            StatusText = $"Exported {Profiles.Count} profile(s) to {path}";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            _toasts.ShowError(StatusText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task ImportProfileAsync()
    {
        var path = await _dialogs.PickJsonFileAsync("Import indexing profile(s)");
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

            var imported = new List<IndexingProfile>();
            foreach (var element in candidates)
            {
                var profile = element.Deserialize<IndexingProfile>(CaptureJsonOptions.Default);
                if (profile is null)
                    continue;

                // A profile can carry executable C# (see Capture.Core.Profiles.FieldScript / IndexField.
                // ScriptExpression) that will run with full system access the next time this profile
                // processes a document, if scripting is enabled — regardless of whether it's enabled
                // right now, since it could be turned on later for an unrelated reason. Warn and require
                // explicit confirmation per profile rather than failing the whole import outright.
                var scriptNames = ScriptNamesIn(profile);
                if (scriptNames.Count > 0 && _dialogs.Host is not null)
                {
                    var list = string.Join(", ", scriptNames);
                    var confirmed = await _confirm.ConfirmAsync(
                        _dialogs.Host,
                        "This profile contains executable code",
                        $"\"{profile.Name}\" includes {(scriptNames.Count == 1 ? "a script" : "scripts")} ({list}) that will run with full access to this computer — the same as any program you'd run yourself — once scripting is enabled in Settings. Only import this if you trust where it came from.",
                        confirmText: "Import anyway",
                        cancelText: "Skip this profile");
                    if (!confirmed)
                        continue;
                }

                // Always import as a new profile — reusing the file's own Id would silently overwrite
                // whatever profile on this machine happens to already have it.
                profile.Id = Guid.NewGuid();
                profile.CreatedUtc = DateTimeOffset.UtcNow;
                await _store.SaveAsync(profile);
                imported.Add(profile);
            }

            if (imported.Count == 0)
            {
                StatusText = "That file doesn't contain a valid indexing profile";
                _toasts.ShowError(StatusText);
                return;
            }

            await ReloadAsync();
            SelectedProfile = Profiles.FirstOrDefault(item => item.Id == imported[^1].Id);
            StatusText = imported.Count == 1
                ? $"Imported \"{imported[0].Name}\""
                : $"Imported {imported.Count} profile(s)";
            _toasts.ShowSuccess(StatusText);
        }
        catch (JsonException)
        {
            StatusText = "That file doesn't contain a valid indexing profile";
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

    private static List<string> ScriptNamesIn(IndexingProfile profile)
    {
        var names = profile.Scripts
            .Where(script => !string.IsNullOrWhiteSpace(script.Source))
            .Select(script => script.Name)
            .ToList();
        if (profile.Fields.Any(field => field.Kind == FieldKind.Script && !string.IsNullOrWhiteSpace(field.ScriptExpression)))
            names.Add("field expression(s)");
        return names;
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
        ExportProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanMutate() => !IsBusy;

    private bool CanEdit() => !IsBusy && SelectedProfile is not null && !IsDesignerOpen;

    private async Task OpenDesignerAsync(IndexingProfile profile, bool isNew)
    {
        var designer = new ProfileDesignerViewModel(profile, isNew, _samples, _store, _redactionSets, _dialogs, _thereforeCategoryPicker, _toasts, _help, _barcodes, _ai, _scriptRunner)
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
