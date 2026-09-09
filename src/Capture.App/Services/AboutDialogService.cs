using Avalonia.Controls;
using Capture.App.Views;

namespace Capture.App.Services;

public sealed class AboutDialogService : IAboutDialogService
{
    private readonly IToastService _toasts;
    private readonly IUpdateCheckService _updateCheck;

    public AboutDialogService(IToastService toasts, IUpdateCheckService updateCheck)
    {
        _toasts = toasts;
        _updateCheck = updateCheck;
    }

    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window)
            return;

        var dialog = new AboutWindow(_updateCheck, _toasts);
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
