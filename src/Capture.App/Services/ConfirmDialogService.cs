using Avalonia.Controls;
using Capture.App.Views;

namespace Capture.App.Services;

public sealed class ConfirmDialogService : IConfirmDialogService
{
    public async Task<bool> ConfirmAsync(object owner, string title, string message, string confirmText = "Continue", string cancelText = "Cancel")
    {
        if (owner is not Window window)
            return false;

        var dialog = new ConfirmWindow(title, message, confirmText, cancelText);
        await dialog.ShowDialog(window);
        return dialog.Confirmed;
    }
}
