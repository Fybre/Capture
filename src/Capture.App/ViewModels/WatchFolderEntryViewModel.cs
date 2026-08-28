using System.Windows.Input;
using Capture.Core.Batches;
using Capture.Core.Profiles;
using Capture.Core.Watch;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed partial class WatchFolderEntryViewModel : ObservableObject
{
    public WatchFolderEntryViewModel(WatchFolderEntry entry)
    {
        Id = entry.Id;
        _enabled = entry.Enabled;
        _folder = entry.Folder ?? string.Empty;
        _settleSeconds = Math.Max(1, entry.SettleMilliseconds / 1000m);
    }

    public Guid Id { get; }

    public Action<WatchFolderEntryViewModel>? BrowseRequested { get; set; }

    public Action<WatchFolderEntryViewModel>? RemoveRequested { get; set; }

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private IndexingProfile? _selectedProfile;

    [ObservableProperty]
    private BatchProfile? _selectedBatchProfile;

    [ObservableProperty]
    private decimal _settleSeconds = 2;

    public ICommand BrowseCommand => new RelayCommand(() => BrowseRequested?.Invoke(this));

    public ICommand RemoveCommand => new RelayCommand(() => RemoveRequested?.Invoke(this));

    public WatchFolderEntry ToModel() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder,
        ProfileId = SelectedProfile?.Id,
        BatchProfileId = SelectedBatchProfile?.Id,
        SettleMilliseconds = (int)(Math.Max(1, SettleSeconds) * 1000)
    };
}
