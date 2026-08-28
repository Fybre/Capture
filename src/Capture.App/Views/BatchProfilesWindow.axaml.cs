using Avalonia.Controls;
using Avalonia.Input;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class BatchProfilesWindow : Window
{
    public BatchProfilesWindow()
    {
        InitializeComponent();
    }

    private async void OnProfilesDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is BatchProfilesViewModel viewModel && viewModel.EditProfileCommand.CanExecute(null))
            await viewModel.EditProfileCommand.ExecuteAsync(null);
    }
}
