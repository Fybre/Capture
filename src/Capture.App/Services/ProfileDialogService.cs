using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Services;

public sealed class ProfileDialogService : IProfileDialogService
{
    private readonly IServiceProvider _services;

    public ProfileDialogService(IServiceProvider services)
    {
        _services = services;
    }

    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window)
            return;

        var viewModel = _services.GetRequiredService<ProfilesViewModel>();
        var dialog = new ProfilesWindow
        {
            DataContext = viewModel
        };
        viewModel.AttachHost(dialog);
        dialog.Opened += async (_, _) => await viewModel.InitializeAsync();
        await dialog.ShowDialog(window);
    }
}
