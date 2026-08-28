using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Services;

public sealed class BatchProfileDialogService : IBatchProfileDialogService
{
    private readonly IServiceProvider _services;

    public BatchProfileDialogService(IServiceProvider services)
    {
        _services = services;
    }

    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window)
            return;

        var viewModel = _services.GetRequiredService<BatchProfilesViewModel>();
        var dialog = new BatchProfilesWindow
        {
            DataContext = viewModel
        };
        viewModel.AttachHost(dialog);
        dialog.Opened += async (_, _) => await viewModel.InitializeAsync();
        await dialog.ShowDialog(window);
    }
}
