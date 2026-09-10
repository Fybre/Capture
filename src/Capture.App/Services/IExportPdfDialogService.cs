namespace Capture.App.Services;

/// <summary>Options collected from the "Export selected to PDF" dialog. <see cref="DestinationPath"/>
/// is a single output file when <see cref="Combine"/> is true, or a destination folder otherwise.</summary>
public sealed record ExportPdfOptions(bool Combine, bool Compress, string DestinationPath);

public interface IExportPdfDialogService
{
    /// <summary>Shows the export dialog; returns null if the user cancelled. <paramref name="suggestedFileName"/>
    /// seeds the save-file picker's editable name — the only rename path, since a single exported PDF is
    /// always named via that picker rather than a separate text box in this dialog.</summary>
    Task<ExportPdfOptions?> ShowAsync(object owner, int documentCount, string suggestedFileName);
}
