using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public interface IAiExtractor
{
    bool IsConfigured { get; }

    /// <summary>Which AI this is, for a document's history — e.g. "gpt-4o-mini at api.openai.com".</summary>
    string Name => "AI";

    Task<IReadOnlyDictionary<Guid, AiExtractedValue>> ExtractAsync(
        string documentText,
        IReadOnlyList<IndexField> fields,
        CancellationToken cancellationToken = default);
}
