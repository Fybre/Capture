namespace Capture.App.ViewModels;

public sealed class DocumentGroupViewModel
{
    public required string Title { get; init; }

    public bool IsUnassigned { get; init; }

    public required IReadOnlyList<string> BatchFieldNames { get; init; }

    public required IReadOnlyList<string> DocumentFieldNames { get; init; }

    public bool HasBatchFields => BatchFieldNames.Count > 0;

    public required IReadOnlyList<DocumentRow> Documents { get; init; }

    public string DocumentCountDisplay => Documents.Count == 1 ? "1 document" : $"{Documents.Count} documents";
}
