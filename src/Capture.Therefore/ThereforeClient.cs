using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Capture.Therefore;

/// <summary>Calls the Therefore REST API (<c>/theservice/v0001/restun/{Operation}</c>) per the
/// documented conventions and a previously-proven reference client
/// (~/Documents/source/therefore-navigator) — always POST, Basic or Bearer auth, and a
/// <c>TenantName</c> header set to exactly what the user typed (never inferred from the URL).</summary>
public sealed class ThereforeClient : IThereforeClient
{
    private readonly HttpClient _http;

    public ThereforeClient(HttpClient httpClient)
    {
        _http = httpClient;
    }

    /// <summary>Normalizes a user-entered base URL: builds
    /// <c>https://{tenant}.thereforeonline.com</c> when left blank, adds a scheme if missing, and
    /// tolerates a URL already ending in <c>/theservice</c>, <c>/theservice/v0001</c>, or
    /// <c>/theservice/v0001/restun</c> so pasting the "wrong" level still works.</summary>
    public static string BuildBaseUrl(string? tenantName, string? baseUrl)
    {
        var server = (baseUrl ?? string.Empty).Trim();
        if (server.Length == 0)
            server = $"https://{tenantName}.thereforeonline.com";
        else if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            server = "https://" + server;

        server = server.TrimEnd('/');
        if (server.EndsWith("/theservice/v0001/restun", StringComparison.OrdinalIgnoreCase))
            return server;
        if (server.EndsWith("/theservice/v0001", StringComparison.OrdinalIgnoreCase))
            return server + "/restun";
        if (server.EndsWith("/theservice", StringComparison.OrdinalIgnoreCase))
            return server + "/v0001/restun";
        return server + "/theservice/v0001/restun";
    }

    public async Task<bool> TestConnectionAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var result = await PostAsync(settings, "GetConnectionToken", new { }, cancellationToken).ConfigureAwait(false);
        return result.RootElement.TryGetProperty("Token", out var token)
            && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(token.GetString());
    }

    public async Task<IReadOnlyList<ThereforeTreeNode>> GetCategoriesTreeAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var result = await PostAsync(settings, "GetCategoriesTree", new { }, cancellationToken).ConfigureAwait(false);
        return result.RootElement.TryGetProperty("TreeItems", out var items) ? ParseTreeItems(items) : [];
    }

    internal static IReadOnlyList<ThereforeTreeNode> ParseTreeItems(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array)
            return [];

        var nodes = new List<ThereforeTreeNode>();
        foreach (var item in items.EnumerateArray())
        {
            // A confirmed live-API quirk: ChildItems can come back as the literal one-element [null]
            // array instead of being empty — a non-object entry here is exactly that, skip it.
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var children = item.TryGetProperty("ChildItems", out var childItems) ? ParseTreeItems(childItems) : [];
            nodes.Add(new ThereforeTreeNode(
                ItemNo: GetInt(item, "ItemNo"),
                ItemType: GetInt(item, "ItemType"),
                Name: GetString(item, "Name"),
                Children: children));
        }

        // Folders first, then alphabetical, then by ItemNo — matches the sort order the previously-
        // built reference tool already uses for this same tree.
        return nodes
            .OrderBy(node => node.ItemType == 1 ? 0 : 1)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.ItemNo)
            .ToList();
    }

    public async Task<ThereforeCategoryInfo> GetCategoryInfoAsync(ThereforeConnectionSettings settings, int categoryNo, CancellationToken cancellationToken = default)
    {
        using var result = await PostAsync(settings, "GetCategoryInfo", new
        {
            CategoryNo = categoryNo,
            IsAccessMaskNeeded = false,
            IsSearchFieldOrderNeeded = false
        }, cancellationToken).ConfigureAwait(false);

        return ParseCategoryInfo(result.RootElement, categoryNo);
    }

    internal static ThereforeCategoryInfo ParseCategoryInfo(JsonElement root, int categoryNo)
    {
        var fields = new List<ThereforeCategoryField>();
        if (root.TryGetProperty("CategoryFields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fieldsEl.EnumerateArray())
            {
                fields.Add(new ThereforeCategoryField(
                    FieldNo: GetInt(field, "FieldNo"),
                    Caption: GetString(field, "Caption"),
                    IndexDataFieldName: GetString(field, "IndexDataFieldName"),
                    FieldType: (ThereforeFieldType)GetInt(field, "FieldType"),
                    Mandatory: GetBool(field, "Mandatory"),
                    IsSingleKeyword: GetBool(field, "IsSingleKeyword"),
                    IsMultipleKeyword: GetBool(field, "IsMultipleKeyword")));
            }
        }

        return new ThereforeCategoryInfo(categoryNo, GetString(root, "Name"), fields);
    }

    public async Task<ThereforeCreateDocumentResult> CreateDocumentAsync(ThereforeConnectionSettings settings, ThereforeCreateDocumentRequest request, CancellationToken cancellationToken = default)
    {
        // PreprocessIndexData validates/defaults the index data (calculated fields, etc.) — feed its
        // returned items into CreateDocument when it gives them back, falling back to what we sent if
        // the response shape doesn't include them.
        var itemsToCreate = request.IndexDataItems;
        using (var preprocessed = await PostAsync(settings, "PreprocessIndexData", new
               {
                   request.CategoryNo,
                   IndexData = new { IndexDataItems = request.IndexDataItems }
               }, cancellationToken).ConfigureAwait(false))
        {
            if (preprocessed.RootElement.TryGetProperty("IndexData", out var indexDataEl)
                && indexDataEl.TryGetProperty("IndexDataItems", out var itemsEl)
                && itemsEl.ValueKind == JsonValueKind.Array)
            {
                itemsToCreate = itemsEl.EnumerateArray().Select(item => (object)item.Clone()).ToList();
            }
        }

        using var result = await PostAsync(settings, "CreateDocument", new
        {
            request.CategoryNo,
            IndexDataItems = itemsToCreate,
            request.Streams,
            request.DoFillDependentFields,
            request.WithAutoAppendMode,
            request.CheckInComments
        }, cancellationToken).ConfigureAwait(false);

        return new ThereforeCreateDocumentResult(GetInt(result.RootElement, "DocNo"));
    }

    private async Task<JsonDocument> PostAsync(ThereforeConnectionSettings settings, string operation, object body, CancellationToken cancellationToken)
    {
        var baseUrl = BuildBaseUrl(settings.TenantName, settings.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{operation}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        // Sent exactly as typed, including empty for on-premise — see BuildBaseUrl's doc comment.
        request.Headers.TryAddWithoutValidation("TenantName", settings.TenantName ?? string.Empty);
        request.Headers.Authorization = settings.AuthMethod == ThereforeAuthMethod.Bearer
            ? new AuthenticationHeaderValue("Bearer", settings.BearerToken ?? string.Empty)
            : new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} failed ({(int)response.StatusCode}): {text}");

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) && value.GetBoolean();
}
