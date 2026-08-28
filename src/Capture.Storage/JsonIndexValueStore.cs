using System.Text.Json;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Paths;

namespace Capture.Storage;

public sealed class JsonIndexValueStore : IIndexValueStore
{
    private readonly IAppPaths _paths;

    public JsonIndexValueStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public Task SaveAsync(
        Guid documentId,
        IReadOnlyList<IndexValue> values,
        CancellationToken cancellationToken = default)
    {
        return WriteAsync(_paths.DocumentIndexesPath(documentId), values, cancellationToken);
    }

    public Task<IReadOnlyList<IndexValue>> GetAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return ReadAsync(_paths.DocumentIndexesPath(documentId), cancellationToken);
    }

    public Task SaveBatchAsync(
        Guid batchId,
        IReadOnlyList<IndexValue> values,
        CancellationToken cancellationToken = default)
    {
        return WriteAsync(_paths.BatchIndexesPath(batchId), values, cancellationToken);
    }

    public Task<IReadOnlyList<IndexValue>> GetBatchAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        return ReadAsync(_paths.BatchIndexesPath(batchId), cancellationToken);
    }

    private static Task WriteAsync(
        string path,
        IReadOnlyList<IndexValue> values,
        CancellationToken cancellationToken)
    {
        // Snapshot now, before LatticeJson's per-path lock — the IndexValue objects backing this list
        // are mutated in place, so without this an earlier-queued save could serialize a later edit's
        // values under an earlier write, which is harmless here since it's still the latest state, but
        // taking the snapshot up front keeps this write's output deterministic regardless of how long
        // it waits behind another in-flight save.
        return LatticeJson.WriteJsonAsync(path, values.ToList(), LatticeJson.Options, cancellationToken);
    }

    private static async Task<IReadOnlyList<IndexValue>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];

        await using var stream = File.OpenRead(path);
        var values = await JsonSerializer.DeserializeAsync<List<IndexValue>>(stream, LatticeJson.Options, cancellationToken)
            .ConfigureAwait(false);
        return values ?? [];
    }
}
