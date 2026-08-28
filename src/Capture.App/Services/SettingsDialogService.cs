using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Services;

public sealed class SettingsDialogService : ISettingsDialogService
{
    private readonly IServiceProvider _services;

    public SettingsDialogService(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<bool> ShowAsync(object owner)
    {
        if (owner is not Window window)
            return false;

        var viewModel = _services.GetRequiredService<SettingsViewModel>();
        var dialog = new SettingsWindow { DataContext = viewModel };
        viewModel.AttachHost(dialog);
        viewModel.Close = () => dialog.Close();
        await viewModel.InitializeAsync();
        await dialog.ShowDialog(window);
        return viewModel.Saved;
    }
}
