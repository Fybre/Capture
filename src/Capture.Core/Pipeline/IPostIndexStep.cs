using Capture.Core.Models;

namespace Capture.Core.Pipeline;

public sealed class PostIndexContext
{
    public required CaptureDocument Document { get; init; }

    public required IReadOnlyList<DocumentPage> Pages { get; init; }

    public required IReadOnlyList<IndexValue> IndexValues { get; init; }
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
