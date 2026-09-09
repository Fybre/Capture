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
}
