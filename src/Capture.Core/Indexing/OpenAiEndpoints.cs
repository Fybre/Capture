using System.Net.Http.Headers;

namespace Capture.Core.Indexing;

/// <summary>Turns what a user pastes as the AI endpoint into the chat completions address, and signs
/// requests the way that service expects. OpenAI and compatible services take a base like
/// <c>https://api.openai.com/v1</c> and a Bearer key; Azure OpenAI and Azure AI Foundry take their
/// resource, deployment or models address and an <c>api-key</c> header.</summary>
public static class OpenAiEndpoints
{
    public const string AzureOpenAiApiVersion = "2024-10-21";
    public const string AzureModelsApiVersion = "2024-05-01-preview";

    public static string CompletionsUrl(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return Legacy(trimmed.TrimEnd('/'));

        var path = uri.AbsolutePath.TrimEnd('/');
        var query = uri.Query.TrimStart('?');
        var root = uri.GetLeftPart(UriPartial.Authority);
        var host = uri.Host.ToLowerInvariant();

        if (IsAzureResource(host))
        {
            var deployments = path.IndexOf("/openai/deployments/", StringComparison.OrdinalIgnoreCase);
            if (deployments >= 0)
            {
                var name = path[(deployments + "/openai/deployments/".Length)..].Split('/')[0];
                return WithApiVersion($"{root}{path[..deployments]}/openai/deployments/{name}/chat/completions", query, AzureOpenAiApiVersion);
            }

            if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                var needsVersion = path.EndsWith("/models/chat/completions", StringComparison.OrdinalIgnoreCase);
                return needsVersion ? WithApiVersion(root + path, query, AzureModelsApiVersion) : Join(root + path, query);
            }

            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return WithApiVersion(root + path + "/chat/completions", query, AzureModelsApiVersion);
            if (path.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
                return Join(root + path + "/chat/completions", query);
            if (path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
                return Join(root + path + "/v1/chat/completions", query);
            // A bare resource address, or a Foundry project address (…/api/projects/x), which isn't
            // where chat requests go: use the resource's OpenAI v1 API, which takes the deployment
            // name as the model.
            return root + "/openai/v1/chat/completions";
        }

        if (host.EndsWith(".models.ai.azure.com", StringComparison.Ordinal))
        {
            // Azure serverless model endpoints answer at /chat/completions, without /v1.
            if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return Join(root + path, query);
            return Join(root + (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? path : string.Empty) + "/chat/completions", query);
        }

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return Join(root + path, query);
        return Join(root + Legacy(path), query);
    }

    /// <summary>True for Azure OpenAI and Azure AI Foundry resources, which want an <c>api-key</c>
    /// header rather than a Bearer key.</summary>
    public static bool UsesApiKeyHeader(string endpoint) =>
        Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) && IsAzureResource(uri.Host.ToLowerInvariant());

    public static void Authorize(HttpRequestMessage request, string endpoint, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return;
        if (UsesApiKeyHeader(endpoint))
            request.Headers.Add("api-key", apiKey.Trim());
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static bool IsAzureResource(string host) =>
        host.EndsWith(".openai.azure.com", StringComparison.Ordinal)
        || host.EndsWith(".services.ai.azure.com", StringComparison.Ordinal)
        || host.EndsWith(".cognitiveservices.azure.com", StringComparison.Ordinal);

    private static string Legacy(string path)
    {
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return path;
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return path + "/chat/completions";
        return path + "/v1/chat/completions";
    }

    private static string Join(string url, string query) => query.Length == 0 ? url : $"{url}?{query}";

    private static string WithApiVersion(string url, string query, string version) =>
        query.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
            ? Join(url, query)
            : Join(url, query.Length == 0 ? $"api-version={version}" : $"{query}&api-version={version}");
}
