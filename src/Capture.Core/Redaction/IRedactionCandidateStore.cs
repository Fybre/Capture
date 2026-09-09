namespace Capture.Core.Redaction;

public interface IRedactionCandidateStore
{
    Task SaveAsync(Guid documentId, IReadOnlyList<RedactionCandidate> candidates, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RedactionCandidate>> GetAsync(Guid documentId, CancellationToken cancellationToken = default);
}
