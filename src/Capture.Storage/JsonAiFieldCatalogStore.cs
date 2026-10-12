using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Core.Indexing;
using Capture.Core.Paths;

namespace Capture.Storage;

public sealed class JsonAiFieldCatalogStore : IAiFieldCatalogStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IAppPaths _paths;

    public JsonAiFieldCatalogStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<AiFieldType>> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        if (!File.Exists(_paths.AiFieldCatalogPath))
        {
            await WriteAsync(AiFieldCatalog.DefaultTypes, cancellationToken).ConfigureAwait(false);
            await WriteOfferedAsync(AiFieldCatalog.DefaultTypes.Select(item => item.Id), cancellationToken).ConfigureAwait(false);
            return AiFieldCatalog.DefaultTypes;
        }

        List<AiFieldType>? types;
        try
        {
            await using var stream = File.OpenRead(_paths.AiFieldCatalogPath);
            types = await JsonSerializer.DeserializeAsync<List<AiFieldType>>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            Trace.TraceError($"Failed to parse AI field catalog at '{_paths.AiFieldCatalogPath}': {ex.Message}. Using built-in defaults.");
            return AiFieldCatalog.DefaultTypes;
        }

        if (types is not { Count: > 0 })
            return AiFieldCatalog.DefaultTypes;

        var offered = await ReadOfferedAsync(cancellationToken).ConfigureAwait(false);
        if (AddNewBuiltIns(types, offered))
            await WriteAsync(types, cancellationToken).ConfigureAwait(false);
        if (!AiFieldCatalog.DefaultTypes.All(item => offered.Contains(item.Id)))
            await WriteOfferedAsync(offered.Union(AiFieldCatalog.DefaultTypes.Select(item => item.Id)), cancellationToken).ConfigureAwait(false);
        return types;
    }

    /// <summary>Adds built-in types introduced since this catalog file was written, each after the last
    /// entry of its classification. A type is offered once (tracked in <paramref name="offered"/>), and only
    /// to a catalog that still uses its classification — so a deleted type stays deleted and a catalog
    /// replaced with the user's own types isn't refilled. Edited entries are left as they are.</summary>
    internal static bool AddNewBuiltIns(List<AiFieldType> types, IReadOnlySet<string> offered)
    {
        var ids = types.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var added = false;
        foreach (var builtIn in AiFieldCatalog.DefaultTypes.Where(item => !ids.Contains(item.Id) && !offered.Contains(item.Id)))
        {
            var last = types.FindLastIndex(item => string.Equals(item.Classification, builtIn.Classification, StringComparison.Ordinal));
            if (last < 0)
                continue;
            types.Insert(last + 1, builtIn);
            added = true;
        }

        return added;
    }

    // Built-in ids this install has already been offered. Absent for a catalog written before this was
    // tracked: the original built-ins (everything up to the ones added alongside it) count as offered.
    private string OfferedPath => Path.ChangeExtension(_paths.AiFieldCatalogPath, ".offered.json");

    private static readonly string[] AddedWithOfferTracking = ["invoice.abn", "invoice.net"];

    private async Task<HashSet<string>> ReadOfferedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(OfferedPath))
            {
                await using var stream = File.OpenRead(OfferedPath);
                var ids = await JsonSerializer.DeserializeAsync<List<string>>(stream, Options, cancellationToken).ConfigureAwait(false);
                if (ids is not null)
                    return ids.ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (JsonException ex)
        {
            Trace.TraceError($"Failed to parse '{OfferedPath}': {ex.Message}.");
        }

        return AiFieldCatalog.DefaultTypes.Select(item => item.Id).Except(AddedWithOfferTracking).ToHashSet(StringComparer.Ordinal);
    }

    private Task WriteOfferedAsync(IEnumerable<string> ids, CancellationToken cancellationToken) =>
        LatticeJson.WriteJsonAsync(OfferedPath, ids.Order(StringComparer.Ordinal).ToList(), Options, cancellationToken);

    private Task WriteAsync(IReadOnlyList<AiFieldType> types, CancellationToken cancellationToken) =>
        LatticeJson.WriteJsonAsync(_paths.AiFieldCatalogPath, types, Options, cancellationToken);
}
