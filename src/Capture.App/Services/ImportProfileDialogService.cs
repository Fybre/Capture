using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Services;

public sealed class ImportProfileDialogService : IImportProfileDialogService
{
    private readonly IServiceProvider _services;
    private readonly IToastService _toasts;

    public ImportProfileDialogService(IServiceProvider services, IToastService toasts)
    {
        _services = services;
        _toasts = toasts;
    }

    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window)
            return;

        var viewModel = _services.GetRequiredService<ImportProfilesViewModel>();
        var dialog = new ImportProfilesWindow
        {
            DataContext = viewModel
        };
        viewModel.AttachHost(dialog);
        dialog.Opened += async (_, _) => await viewModel.InitializeAsync();
        _toasts.AttachHost(dialog);
        try
        {
            await dialog.ShowDialog(window);
        }
        finally
        {
            _toasts.DetachHost(dialog);
        }
    }
}
