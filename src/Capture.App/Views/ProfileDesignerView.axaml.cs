using Avalonia.Controls;
using Avalonia.Input;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class ProfileDesignerView : UserControl
{
    public ProfileDesignerView()
    {
        InitializeComponent();
    }

    private void OnKeyPatternGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is ProfileDesignerViewModel viewModel)
            viewModel.BeginSuggestKey();
    }

    private void OnValuePatternGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is ProfileDesignerViewModel viewModel)
            viewModel.BeginSuggestValue();
    }

    private void OnClearPatternGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is ProfileDesignerViewModel viewModel)
            viewModel.SuggestTarget = ProfileDesignerViewModel.PatternSuggestTarget.None;
    }

    private const double ZoomButtonStep = 1.25;

    private void OnZoomInClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PreviewImage.Zoom *= ZoomButtonStep;

    private void OnZoomOutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PreviewImage.Zoom /= ZoomButtonStep;

    private void OnZoomResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PreviewImage.Zoom = Capture.App.Controls.PagePreview.MinZoom;
}
