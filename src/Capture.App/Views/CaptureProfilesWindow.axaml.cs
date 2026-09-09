using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capture.App.ViewModels;
using Capture.Core.CaptureProfiles;

namespace Capture.App.Views;

public partial class CaptureProfilesWindow : Window
{
    public CaptureProfilesWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only activate an actual profile row. A double-click in the ListBox's empty area must not
        // reopen whichever profile happened to remain selected previously.
        var item = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not CaptureProfile profile || DataContext is not CaptureProfilesViewModel viewModel)
            return;

        viewModel.SelectedProfile = profile;
        if (viewModel.EditCommand.CanExecute(null))
            viewModel.EditCommand.Execute(null);
        e.Handled = true;
    }
}
