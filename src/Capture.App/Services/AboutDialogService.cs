using Avalonia.Controls;
using Capture.App.Views;

namespace Capture.App.Services;

public sealed class AboutDialogService : IAboutDialogService
{
    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window)
            return;

        await new AboutWindow().ShowDialog(window);
    }
}
