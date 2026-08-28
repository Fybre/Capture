using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public interface IAiExtractor
{
    bool IsConfigured { get; }

    Task<IReadOnlyDictionary<Guid, AiExtractedValue>> ExtractAsync(
        string documentText,
        IReadOnlyList<IndexField> fields,
        CancellationToken cancellationToken = default);
}
