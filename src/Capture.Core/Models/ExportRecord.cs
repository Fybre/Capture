using Capture.Core.Profiles;

namespace Capture.Core.Models;

/// <summary>One run of one export for a document: where it went and what it produced, or why it failed.</summary>
/// <param name="ExportId">The ExportDefinition that ran.</param>
/// <param name="ExportName">Its name at the time, kept in case the export is later renamed or removed.</param>
/// <param name="Reference">What it produced, e.g. "Therefore document 1234" or the file written.</param>
/// <param name="Message">Why it failed; null when it succeeded.</param>
public sealed record ExportRecord(
    Guid ExportId,
    string ExportName,
    ExportType ExportType,
    DateTimeOffset Utc,
    bool Success,
    string? Reference,
    string? Message);
