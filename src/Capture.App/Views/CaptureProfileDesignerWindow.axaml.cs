using Avalonia.Controls;
using Capture.App.Services;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class CaptureProfileDesignerWindow : Window
{
    public CaptureProfileDesignerWindow() => InitializeComponent();

    /// <summary>Set by the dialog service before showing the window; enables the unsaved-changes prompt
    /// on close. Left null in designer-only construction (e.g. previewers) where closing is a no-op.</summary>
    public IConfirmDialogService? ConfirmService { get; set; }

    private bool _forceClose;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose || ConfirmService is null
            || DataContext is not CaptureProfileDesignerViewModel viewModel
            || !viewModel.HasUnsavedChanges())
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = ConfirmCloseAsync();
        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        var discard = await ConfirmService!.ConfirmAsync(
            this,
            "Discard unsaved changes?",
            "This capture profile has unsaved changes. Close without saving?",
            "Discard changes",
            "Keep editing");
        if (!discard) return;
        _forceClose = true;
        Close();
    }
}
