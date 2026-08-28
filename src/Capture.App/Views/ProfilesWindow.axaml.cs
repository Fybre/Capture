using Avalonia.Controls;
using Avalonia.Input;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class ProfilesWindow : Window
{
    public ProfilesWindow()
    {
        InitializeComponent();
    }

    private async void OnProfilesDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ProfilesViewModel viewModel && viewModel.EditProfileCommand.CanExecute(null))
            await viewModel.EditProfileCommand.ExecuteAsync(null);
    }
}
