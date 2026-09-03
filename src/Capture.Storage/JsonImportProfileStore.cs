using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Core.Import;
using Capture.Core.Paths;

namespace Capture.Storage;

public sealed class JsonImportProfileStore : IImportProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IAppPaths _paths;

    public JsonImportProfileStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<ImportProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!Directory.Exists(_paths.ImportProfilesDirectory))
            return [];

        var results = new List<ImportProfile>();
        foreach (var file in Directory.EnumerateFiles(_paths.ImportProfilesDirectory, "import-profile.json", SearchOption.AllDirectories))
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

    public Task<ImportProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(_paths.ImportProfileJsonPath(id), cancellationToken);
    }

    public Task SaveAsync(ImportProfile profile, CancellationToken cancellationToken = default)
    {
        profile.ModifiedUtc = DateTimeOffset.UtcNow;
        return LatticeJson.WriteJsonAsync(_paths.ImportProfileJsonPath(profile.Id), profile, Options, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _paths.ImportProfileDirectory(id);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<ImportProfile?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ImportProfile>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }
}
