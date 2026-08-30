using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

public sealed record ExportResult(bool Success, string? Message);

public sealed record ExportDocumentContext(
    CaptureDocument Document,
    IReadOnlyList<IndexField> ProfileFields,
    IReadOnlyList<IndexValue> IndexValues);

public interface IExportWriter
{
    ExportType Type { get; }

    Task<ExportResult> ExportAsync(
        ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default);
}
