using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Core.Batches;
using Capture.Core.Paths;

namespace Capture.Storage;

public sealed class JsonBatchProfileStore : IBatchProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IAppPaths _paths;

    public JsonBatchProfileStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<BatchProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!Directory.Exists(_paths.BatchProfilesDirectory))
            return [];

        var results = new List<BatchProfile>();
        foreach (var file in Directory.EnumerateFiles(_paths.BatchProfilesDirectory, "batch-profile.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
            if (profile is not null)
                results.Add(profile);
        }

        return results
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<BatchProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(_paths.BatchProfileJsonPath(id), cancellationToken);
    }

    public Task SaveAsync(BatchProfile profile, CancellationToken cancellationToken = default)
    {
        profile.ModifiedUtc = DateTimeOffset.UtcNow;
        return LatticeJson.WriteJsonAsync(_paths.BatchProfileJsonPath(profile.Id), profile, Options, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _paths.BatchProfileDirectory(id);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<BatchProfile?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BatchProfile>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }
}
