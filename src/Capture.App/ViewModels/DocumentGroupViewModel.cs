namespace Capture.App.ViewModels;

public sealed class DocumentGroupViewModel
{
    public required string Title { get; init; }

    public bool IsUnassigned { get; init; }

    public required IReadOnlyList<string> BatchFieldNames { get; init; }

    public required IReadOnlyList<string> DocumentFieldNames { get; init; }

    public bool HasBatchFields => BatchFieldNames.Count > 0;

    public required IReadOnlyList<DocumentRow> Documents { get; init; }

    /// <summary>What the grid actually binds to — <see cref="Documents"/> with a <see cref="BatchDividerRow"/>
    /// spliced in before the first document of each batch. Built by MainViewModel.BuildDisplayRows;
    /// <see cref="Documents"/> itself stays a plain document list for every other consumer (field-name
    /// derivation, selection bookkeeping).</summary>
    public required IReadOnlyList<object> DisplayRows { get; init; }

    public string DocumentCountDisplay => Documents.Count == 1 ? "1 document" : $"{Documents.Count} documents";
}
