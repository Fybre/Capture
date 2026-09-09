using Avalonia.Controls;
using Capture.App.Views;

namespace Capture.App.Services;

public sealed class ScriptEditorDialogService : IScriptEditorDialogService
{
    public async Task<string?> EditAsync(object owner, string title, string source)
    {
        if (owner is not Window window)
            return null;

        var dialog = new ScriptEditorWindow(title, source);
        await dialog.ShowDialog(window);
        return dialog.Saved ? dialog.Text : null;
    }
}
