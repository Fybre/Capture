namespace Capture.Core.Indexing;

public static class OpenAiEndpoints
{
    public static string CompletionsUrl(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return trimmed + "/chat/completions";
        return trimmed + "/v1/chat/completions";
    }
}
