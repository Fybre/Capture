using Avalonia.Controls;
using Avalonia.Input;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class ImportProfilesWindow : Window
{
    public ImportProfilesWindow()
    {
        InitializeComponent();
    }

    private async void OnProfilesDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ImportProfilesViewModel viewModel && viewModel.EditProfileCommand.CanExecute(null))
            await viewModel.EditProfileCommand.ExecuteAsync(null);
    }
}
