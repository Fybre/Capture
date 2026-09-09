using System.Text.Json;
using Capture.Core.CaptureProfiles;
using Capture.Core.Paths;

namespace Capture.Storage;

public sealed class JsonCaptureProfileStore(IAppPaths paths) : ICaptureProfileStore
{
    public async Task<IReadOnlyList<CaptureProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var results = new List<CaptureProfile>();
        foreach (var file in Directory.EnumerateFiles(paths.CaptureProfilesDirectory, "capture-profile.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(file);
            var profile = await JsonSerializer.DeserializeAsync<CaptureProfile>(stream, CaptureJsonOptions.Default, cancellationToken).ConfigureAwait(false);
            if (profile is not null) results.Add(profile);
        }
        return results.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<CaptureProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = paths.CaptureProfileJsonPath(id);
        if (!File.Exists(file)) return null;
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<CaptureProfile>(stream, CaptureJsonOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken = default)
    {
        profile.ModifiedUtc = DateTimeOffset.UtcNow;
        return LatticeJson.WriteJsonAsync(paths.CaptureProfileJsonPath(profile.Id), profile, CaptureJsonOptions.Default, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = paths.CaptureProfileDirectory(id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }
}
