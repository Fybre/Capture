using Avalonia.Controls;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel viewModel)
                viewModel.ShowConnectionEditor = ShowConnectionEditorAsync;
        };
    }

    private async Task ShowConnectionEditorAsync(ConnectionEditorViewModel editor)
    {
        var dialog = new ConnectionWindow { DataContext = editor };
        editor.Close = dialog.Close;
        var toasts = (DataContext as SettingsViewModel)?.Toasts;
        toasts?.AttachHost(dialog);
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            toasts?.DetachHost(dialog);
        }
    }
}
