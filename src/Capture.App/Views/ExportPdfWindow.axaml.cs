using Avalonia.Controls;
using Avalonia.Interactivity;
using Capture.App.Services;

namespace Capture.App.Views;

public partial class ExportPdfWindow : Window
{
    private readonly IFileDialogService? _dialogs;
    private readonly string _suggestedFileName = "Export.pdf";
    private string? _destinationPath;

    public ExportPdfOptions? Result { get; private set; }

    public ExportPdfWindow()
    {
        InitializeComponent();
    }

    public ExportPdfWindow(IFileDialogService dialogs, int documentCount, string suggestedFileName) : this()
    {
        _dialogs = dialogs;
        _suggestedFileName = suggestedFileName;
        SummaryText.Text = documentCount == 1
            ? "Export 1 selected document"
            : $"Export {documentCount} selected documents";

        // Combining is meaningless with a single document — force it on (a single output file, named
        // via the save-file picker below) and take the choice off the table rather than leave a
        // checkbox on screen that can't do anything.
        if (documentCount <= 1)
        {
            CombineCheckBox.IsChecked = true;
            CombineCheckBox.IsEnabled = false;
            ToolTip.SetTip(CombineCheckBox, "Only one document is selected, so there's nothing to combine");
        }
    }

    private void OnCombineChanged(object? sender, RoutedEventArgs e)
    {
        // Location choice depends on Combine (a single file vs. a destination folder) — clear
        // whatever was picked under the old mode rather than silently reusing a mismatched path.
        _destinationPath = null;
        LocationText.Text = "No location chosen";
        ExportButton.IsEnabled = false;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (_dialogs is null)
            return;

        var path = CombineCheckBox.IsChecked == true
            ? await _dialogs.PickSaveFilePdfAsync("Export selected to PDF", _suggestedFileName)
            : await _dialogs.PickFolderAsync("Choose a destination folder");

        if (string.IsNullOrWhiteSpace(path))
            return;

        _destinationPath = path;
        LocationText.Text = path;
        ExportButton.IsEnabled = true;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_destinationPath is null)
            return;

        Result = new ExportPdfOptions(CombineCheckBox.IsChecked == true, CompressCheckBox.IsChecked == true, _destinationPath);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
