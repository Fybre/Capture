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
            return AiFieldCatalog.DefaultTypes;
        }

        try
        {
            await using var stream = File.OpenRead(_paths.AiFieldCatalogPath);
            var types = await JsonSerializer.DeserializeAsync<List<AiFieldType>>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
            return types is { Count: > 0 } ? types : AiFieldCatalog.DefaultTypes;
        }
        catch (JsonException ex)
        {
            Trace.TraceError($"Failed to parse AI field catalog at '{_paths.AiFieldCatalogPath}': {ex.Message}. Using built-in defaults.");
            return AiFieldCatalog.DefaultTypes;
        }
    }

    private Task WriteAsync(IReadOnlyList<AiFieldType> types, CancellationToken cancellationToken) =>
        LatticeJson.WriteJsonAsync(_paths.AiFieldCatalogPath, types, Options, cancellationToken);
}
