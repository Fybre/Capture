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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
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
