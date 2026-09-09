using Capture.Core.Models;
using Capture.Core.CaptureProfiles;

namespace Capture.Core.Pipeline;

public sealed class PostIndexContext
{
    public required CaptureDocument Document { get; init; }

    public required IReadOnlyList<DocumentPage> Pages { get; init; }

    /// <summary>Fields extracted from this document only. Batch fields are shared metadata and must
    /// never be interpreted as coordinates on each document's pages.</summary>
    public required IReadOnlyList<IndexValue> DocumentIndexValues { get; init; }

    public required DocumentTypeDefinition Profile { get; init; }
}

/// <summary>
/// A pluggable step that runs after a document's fields have been extracted and saved
/// (e.g. redaction). A step's own failure must never abort the import — callers are expected
/// to catch and log rather than let an exception here fail document processing.
/// </summary>
public interface IPostIndexStep
{
    Task RunAsync(PostIndexContext context, CancellationToken cancellationToken = default);
}
