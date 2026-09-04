using Capture.Core.Models;

namespace Capture.Core.Import;

public sealed class ImportedDocument
{
    public required CaptureDocument Document { get; init; }

    public IReadOnlyDictionary<Guid, string> SeparatorValues { get; init; } =
        new Dictionary<Guid, string>();

    /// <summary>Whether this document's boundary was triggered by a batch profile's barcode/regex detector.</summary>
    public bool StartsNewBatch { get; init; }

    /// <summary>The value the batch trigger captured, when <see cref="StartsNewBatch"/> is true.</summary>
    public string? BatchSeparatorValue { get; init; }

    /// <summary>The batch profile's own <c>Fields</c> captured at the moment its boundary was detected
    /// (before this page might be discarded), when <see cref="StartsNewBatch"/> is true — see
    /// <c>BatchSeparator.DetectAsync</c>/<c>BatchTriggerHit.CapturedFields</c>. Only ever meaningful for
    /// the one document that actually started the batch; every other document in it reads these back
    /// via <c>IIndexValueStore.GetBatchAsync</c> instead.</summary>
    public IReadOnlyList<IndexValue> CapturedBatchFields { get; init; } = [];
}
