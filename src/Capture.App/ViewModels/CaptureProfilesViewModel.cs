using System.Collections.ObjectModel;
using Capture.Core.CaptureProfiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class CaptureProfilesViewModel(ICaptureProfileStore store) : ViewModelBase
{
    public ObservableCollection<CaptureProfile> Profiles { get; } = [];
    public Func<CaptureProfile, Task>? EditRequested { get; set; }
    public Func<CaptureProfile, Task<bool>>? DeleteConfirmed { get; set; }
    public Func<CaptureProfile, Task>? ExportRequested { get; set; }
    public Func<Task>? ExportAllRequested { get; set; }
    public Func<Task<CaptureProfile?>>? ImportRequested { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private CaptureProfile? _selectedProfile;

    public async Task InitializeAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task NewAsync()
    {
        var profile = new CaptureProfile { Enabled = false };
        if (EditRequested is not null) await EditRequested(profile);
        await ReloadAsync(profile.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedProfile is not { } profile || EditRequested is null) return;
        await EditRequested(profile);
        await ReloadAsync(profile.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedProfile is not { } profile) return;
        if (DeleteConfirmed is not null && !await DeleteConfirmed(profile)) return;
        await store.DeleteAsync(profile.Id);
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportAsync()
    {
        if (SelectedProfile is not { } profile || ExportRequested is null) return;
        await ExportRequested(profile);
    }

    [RelayCommand]
    private async Task ExportAllAsync()
    {
        if (ExportAllRequested is null) return;
        await ExportAllRequested();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (ImportRequested is null) return;
        var imported = await ImportRequested();
        if (imported is not null) await ReloadAsync(imported.Id);
    }

    private bool HasSelection() => SelectedProfile is not null;

    private async Task ReloadAsync(Guid? selectId = null)
    {
        Profiles.Clear();
        foreach (var profile in await store.GetAllAsync()) Profiles.Add(profile);
        SelectedProfile = selectId is { } id
            ? Profiles.FirstOrDefault(profile => profile.Id == id)
            : Profiles.FirstOrDefault();
    }
}
