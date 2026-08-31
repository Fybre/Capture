using Avalonia.Controls;
using Capture.App.Views;

namespace Capture.App.Services;

public sealed class AboutDialogService : IAboutDialogService
{
    private readonly IToastService _toasts;

    public AboutDialogService(IToastService toasts)
    {
        _toasts = toasts;
    }

    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window)
            return;

        var dialog = new AboutWindow();
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
