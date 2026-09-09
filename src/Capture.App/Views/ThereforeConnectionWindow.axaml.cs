using Avalonia.Controls;

namespace Capture.App.Views;

public partial class ThereforeConnectionWindow : Window
{
    public ThereforeConnectionWindow()
    {
        InitializeComponent();
    }

    private void OnDoneClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
