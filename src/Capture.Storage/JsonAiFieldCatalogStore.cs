using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Core.Indexing;
using Capture.Core.Paths;

namespace Capture.Storage;

public sealed class JsonAiFieldCatalogStore : IAiFieldCatalogStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // The built-ins that shipped with offer tracking itself: a catalog from before then is offered these.
    private static readonly string[] AddedWithOfferTracking = ["invoice.abn", "invoice.net"];

    private readonly IAppPaths _paths;

    public JsonAiFieldCatalogStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public Task<IReadOnlyList<AiFieldType>> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        return new BuiltInCatalogFile<AiFieldType>(
                _paths.AiFieldCatalogPath, AiFieldCatalog.DefaultTypes, item => item.Id, item => item.Classification,
                Options, AddedWithOfferTracking.Concat(NewSinceOfferTracking()).ToList())
            .LoadAsync(cancellationToken);
    }

    // Built-ins added after offer tracking shipped never count as offered to a catalog without a sidecar.
    private static IEnumerable<string> NewSinceOfferTracking() =>
        AiFieldCatalog.DefaultTypes.Select(item => item.Id).Where(id =>
            id.StartsWith("credit.", StringComparison.Ordinal)
            || id.StartsWith("statement.", StringComparison.Ordinal)
            || id.StartsWith("remittance.", StringComparison.Ordinal));
}

public sealed class JsonAiDocumentTypeStore : IAiDocumentTypeStore
{
    private readonly IAppPaths _paths;

    public JsonAiDocumentTypeStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public Task<IReadOnlyList<AiDocumentType>> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        return new BuiltInCatalogFile<AiDocumentType>(
                _paths.AiDocumentTypesPath, AiDocumentTypeCatalog.DefaultTypes, item => item.Id, _ => string.Empty,
                JsonAiFieldCatalogStore.Options)
            .LoadAsync(cancellationToken);
    }
}
