using Avalonia.Controls;
using Capture.App.Views;

namespace Capture.App.Services;

public sealed class ExportPdfDialogService(IFileDialogService dialogs) : IExportPdfDialogService
{
    public async Task<ExportPdfOptions?> ShowAsync(object owner, int documentCount)
    {
        if (owner is not Window window)
            return null;

        var dialog = new ExportPdfWindow(dialogs, documentCount);
        await dialog.ShowDialog(window);
        return dialog.Result;
    }
}
