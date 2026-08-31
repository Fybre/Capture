using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Services;

public sealed class ThereforeCategoryPickerDialogService : IThereforeCategoryPickerDialogService
{
    private readonly IServiceProvider _services;
    private readonly IToastService _toasts;

    public ThereforeCategoryPickerDialogService(IServiceProvider services, IToastService toasts)
    {
        _services = services;
        _toasts = toasts;
    }

    public async Task<ThereforeCategorySelection?> ShowAsync(object owner)
    {
        if (owner is not Window window)
            return null;

        var viewModel = _services.GetRequiredService<ThereforeCategoryPickerViewModel>();
        var dialog = new ThereforeCategoryPickerWindow { DataContext = viewModel };
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
        return viewModel.Result;
    }
}
