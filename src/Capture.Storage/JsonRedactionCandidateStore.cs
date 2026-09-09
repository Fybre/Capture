using System.Text.Json;
using Capture.Core.Paths;
using Capture.Core.Redaction;

namespace Capture.Storage;

public sealed class JsonRedactionCandidateStore : IRedactionCandidateStore
{
    private readonly IAppPaths _paths;

    public JsonRedactionCandidateStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public Task SaveAsync(Guid documentId, IReadOnlyList<RedactionCandidate> candidates, CancellationToken cancellationToken = default)
    {
        return LatticeJson.WriteJsonAsync(
            _paths.DocumentRedactionCandidatesPath(documentId), candidates.ToList(), LatticeJson.Options, cancellationToken);
    }

    public async Task<IReadOnlyList<RedactionCandidate>> GetAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var path = _paths.DocumentRedactionCandidatesPath(documentId);
        if (!File.Exists(path))
            return [];

        await using var stream = File.OpenRead(path);
        var candidates = await JsonSerializer.DeserializeAsync<List<RedactionCandidate>>(stream, LatticeJson.Options, cancellationToken)
            .ConfigureAwait(false);
        return candidates ?? [];
    }
}
