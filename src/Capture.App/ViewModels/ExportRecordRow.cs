using Capture.Core.Models;

namespace Capture.App.ViewModels;

/// <summary>One line of a document's export history in the review panel.</summary>
public sealed class ExportRecordRow(ExportRecord record)
{
    public ExportRecord Record { get; } = record;

    public bool Success => Record.Success;

    public string Title => $"{(Record.Success ? "✓" : "✗")} {Record.ExportName}";

    /// <summary>What it produced, or why it failed.</summary>
    public string Detail => Record.Success ? Record.Reference ?? "Exported" : Record.Message ?? "Failed";

    public string When => Record.Utc.ToLocalTime().ToString("d MMM yyyy, HH:mm");
}
