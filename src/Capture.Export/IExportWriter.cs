using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

public sealed record ExportResult(bool Success, string? Message);

public sealed record ExportDocumentContext(
    CaptureDocument Document,
    IReadOnlyList<IndexField> ProfileFields,
    IReadOnlyList<IndexValue> IndexValues)
{
    /// <summary>Batch-level field definitions available to every document in the batch. Kept
    /// separate from <see cref="ProfileFields"/> so exporters and their UIs can retain scope even
    /// when a batch field and document field share the same display name.</summary>
    public IReadOnlyList<IndexField> BatchFields { get; init; } = [];

    public IReadOnlyList<IndexField> AllProfileFields => BatchFields.Concat(ProfileFields).ToList();
}

public interface IExportWriter
{
    ExportType Type { get; }

    Task<ExportResult> ExportAsync(
        ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default);
}
