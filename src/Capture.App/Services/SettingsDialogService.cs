using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Services;

public sealed class SettingsDialogService : ISettingsDialogService
{
    private readonly IServiceProvider _services;
    private readonly IToastService _toasts;

    public SettingsDialogService(IServiceProvider services, IToastService toasts)
    {
        _services = services;
        _toasts = toasts;
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
        _toasts.AttachHost(dialog);
        try
        {
            await dialog.ShowDialog(window);
        }
        finally
        {
            _toasts.DetachHost(dialog);
        }
        return viewModel.Saved;
    }
}
