using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Services;

public sealed class ThereforeCategoryPickerDialogService : IThereforeCategoryPickerDialogService
{
    private readonly IServiceProvider _services;

    public ThereforeCategoryPickerDialogService(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<ThereforeCategorySelection?> ShowAsync(object owner)
    {
        if (owner is not Window window)
            return null;

        var viewModel = _services.GetRequiredService<ThereforeCategoryPickerViewModel>();
        var dialog = new ThereforeCategoryPickerWindow { DataContext = viewModel };
        viewModel.Close = () => dialog.Close();
        await viewModel.InitializeAsync();
        await dialog.ShowDialog(window);
        return viewModel.Result;
    }
}
