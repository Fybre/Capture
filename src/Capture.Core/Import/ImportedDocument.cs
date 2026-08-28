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
}
