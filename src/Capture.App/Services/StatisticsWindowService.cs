using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Capture.Core.CaptureProfiles;
using Capture.Core.Store;

namespace Capture.App.Services;

public sealed class StatisticsWindowService : IStatisticsWindowService
{
    private readonly IDocumentStore _store;
    private readonly ICaptureProfileStore _profiles;

    public StatisticsWindowService(IDocumentStore store, ICaptureProfileStore profiles)
    {
        _store = store;
        _profiles = profiles;
    }

    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window)
            return;

        var viewModel = new StatisticsViewModel(_store, _profiles);
        var dialog = new StatisticsWindow { DataContext = viewModel };
        _ = viewModel.LoadAsync();
        await dialog.ShowDialog(window);
    }
}
