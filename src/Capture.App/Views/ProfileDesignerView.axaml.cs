using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class ProfileDesignerView : UserControl
{
    public ProfileDesignerView()
    {
        InitializeComponent();

        // Switching fields shouldn't leave the properties pane scrolled to wherever the previous
        // field's (possibly much longer) panel left it — reset to the top so Name is visible again.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ProfileDesignerViewModel viewModel)
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileDesignerViewModel.SelectedField))
            FieldPropertiesScroll.Offset = new Vector(0, 0);
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
