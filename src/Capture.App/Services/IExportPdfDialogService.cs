namespace Capture.App.Services;

/// <summary>Options collected from the "Export selected to PDF" dialog. <see cref="DestinationPath"/>
/// is a single output file when <see cref="Combine"/> is true, or a destination folder otherwise.</summary>
public sealed record ExportPdfOptions(bool Combine, bool Compress, string DestinationPath);

public interface IExportPdfDialogService
{
    /// <summary>Shows the export dialog; returns null if the user cancelled.</summary>
    Task<ExportPdfOptions?> ShowAsync(object owner, int documentCount);
}
