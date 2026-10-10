using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.CaptureProfiles;

namespace Capture.Core.Indexing;

public interface IProfileApplicator
{
    IReadOnlyList<IndexValue> Apply(
        DocumentTypeDefinition documentType,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null);

    Task<IReadOnlyList<IndexValue>> ApplyAsync(
        DocumentTypeDefinition documentType,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a document type or batch definition's fields and scripts.</summary>
    Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IReadOnlyList<IndexField> fields,
        IReadOnlyList<FieldScript> scripts,
        string sharedScriptSource,
        IReadOnlyList<PageLattice> lattices,
        string? profileName = null,
        string? locale = null,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null,
        CancellationToken cancellationToken = default);

    /// <summary>Re-works out the fields that are computed from other fields (Script fields, and Text or
    /// Lookup fields with a template) after someone edits a value in review. Fields edited by hand are
    /// left alone, and extraction isn't run again. Returns true when any value changed.</summary>
    Task<bool> RecalculateAsync(
        IReadOnlyList<IndexField> fields,
        string sharedScriptSource,
        IReadOnlyList<IndexValue> values,
        IReadOnlyList<PageLattice> lattices,
        string? profileName = null,
        string? locale = null,
        DefaultValueContext? context = null,
        CaptureDocument? document = null,
        CancellationToken cancellationToken = default);
}
