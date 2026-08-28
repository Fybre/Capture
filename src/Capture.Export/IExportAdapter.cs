using Capture.Core.Models;

namespace Capture.Export;

public sealed record ExportResult(bool Success, string? Message);

public sealed record ExportMapping(Guid FieldId, string ExternalName, string? ExternalType);

public interface IExportAdapter
{
    string Id { get; }
    string Name { get; }
    bool IsConfigured { get; }

    Task<ExportResult> ExportAsync(CaptureDocument document, CancellationToken cancellationToken = default);
}
