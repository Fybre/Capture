using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Capture.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    // The Therefore connection dialog shares this window's own SettingsViewModel as its DataContext
    // (not a copy) — edits made there land directly on the same observable properties this window's
    // own Save button persists, so there's no separate save step in the nested dialog itself.
    private async void OnConfigureThereforeClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new ThereforeConnectionWindow { DataContext = DataContext };
        await dialog.ShowDialog(this);
    }
}
