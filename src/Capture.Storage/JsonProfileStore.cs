using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Core.Paths;
using Capture.Core.Profiles;

namespace Capture.Storage;

public sealed class JsonProfileStore : IProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IAppPaths _paths;

    public JsonProfileStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<IndexingProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!Directory.Exists(_paths.ProfilesDirectory))
            return [];

        var results = new List<IndexingProfile>();
        foreach (var file in Directory.EnumerateFiles(_paths.ProfilesDirectory, "profile.json", SearchOption.AllDirectories))
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

    public Task<IndexingProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(_paths.ProfileJsonPath(id), cancellationToken);
    }

    public async Task SaveAsync(IndexingProfile profile, CancellationToken cancellationToken = default)
    {
        profile.ModifiedUtc = DateTimeOffset.UtcNow;
        var path = _paths.ProfileJsonPath(profile.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profile, Options, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _paths.ProfileDirectory(id);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<IndexingProfile?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        var profile = await JsonSerializer.DeserializeAsync<IndexingProfile>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
        if (profile is not null)
        {
            MigrateLegacySeparation(profile);
            MigrateLegacyBarcodePageScope(profile);
        }

        return profile;
    }

    // Barcode fields never had a meaningful PageScope before it became a real, chosen setting — First
    // was just IndexField's unused default, so every barcode field saved before now has "first" sitting
    // in its JSON regardless of what page it actually targets. Reinterpreting that as a real First choice
    // would silently move extraction to page 1. PageScopeConfigured (added alongside this feature) tells
    // an untouched legacy default apart from a deliberate choice — only the former gets normalized to
    // Number (today's implicit "read from this one fixed page" behavior).
    private static void MigrateLegacyBarcodePageScope(IndexingProfile profile)
    {
        foreach (var field in profile.Fields.Where(item =>
                     item.Kind == FieldKind.Barcode && item.PageScope == PageScope.First && !item.PageScopeConfigured))
            field.PageScope = PageScope.Number;
    }

    // Profiles saved before document separation moved to IndexingProfile.Separation only had a
    // profile-wide SplitOnBlankPages bool and/or a barcode field flagged SeparatesDocuments. Turn
    // whichever was set into the equivalent Separation config so existing profiles keep working.
    private static void MigrateLegacySeparation(IndexingProfile profile)
    {
        if (profile.Separation.Trigger != DocumentSeparationTrigger.None)
            return;

        if (profile.SplitOnBlankPages)
        {
            profile.Separation = new DocumentSeparation
            {
                Trigger = DocumentSeparationTrigger.BlankPage,
                DiscardSeparatorPage = true
            };
            return;
        }

        var separatorField = profile.Fields.FirstOrDefault(field => field.Kind == FieldKind.Barcode && field.SeparatesDocuments);
        if (separatorField is not null)
        {
            profile.Separation = new DocumentSeparation
            {
                Trigger = DocumentSeparationTrigger.Barcode,
                BarcodeFieldId = separatorField.Id,
                DiscardSeparatorPage = separatorField.DiscardPage
            };
        }
    }
}
