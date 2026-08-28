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

    private static async Task WriteAsync(
        string path,
        IReadOnlyList<IndexValue> values,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, values.ToList(), LatticeJson.Options, cancellationToken)
            .ConfigureAwait(false);
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
