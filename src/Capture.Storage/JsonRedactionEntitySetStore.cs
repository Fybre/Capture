using System.Text.Json;
using Capture.Core.Paths;
using Capture.Core.Redaction;

namespace Capture.Storage;

public sealed class JsonRedactionEntitySetStore : IRedactionEntitySetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IAppPaths _paths;

    public JsonRedactionEntitySetStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<RedactionEntitySet>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!Directory.Exists(_paths.RedactionSetsDirectory))
            return [];

        var results = new List<RedactionEntitySet>();
        foreach (var file in Directory.EnumerateFiles(_paths.RedactionSetsDirectory, "redaction-set.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var set = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
            if (set is not null)
                results.Add(set);
        }

        return results
            .OrderBy(set => set.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<RedactionEntitySet?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(_paths.RedactionSetJsonPath(id), cancellationToken);
    }

    public Task SaveAsync(RedactionEntitySet set, CancellationToken cancellationToken = default)
    {
        set.ModifiedUtc = DateTimeOffset.UtcNow;
        return LatticeJson.WriteJsonAsync(_paths.RedactionSetJsonPath(set.Id), set, Options, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _paths.RedactionSetDirectory(id);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<RedactionEntitySet?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RedactionEntitySet>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }
}
