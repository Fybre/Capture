using Avalonia.Controls;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class FirstRunWizardWindow : Window
{
    public FirstRunWizardWindow()
    {
        InitializeComponent();
    }

    public FirstRunWizardWindow(FirstRunWizardViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Close = Close;
    }
}
