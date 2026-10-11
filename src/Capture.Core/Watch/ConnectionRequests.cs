using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Capture.Core.Watch;

/// <summary>Builds signed requests for a <see cref="ConnectionDefinition"/> — the one place that turns a
/// connection's stored credentials into headers. Requests can only go to the connection's own server.</summary>
public static class ConnectionRequests
{
    /// <summary>A request to <paramref name="path"/>, relative to the connection's address (a full URL is
    /// accepted only on the same server), with the connection's sign-in headers added. Without a body, a
    /// Therefore POST sends <c>{}</c>, which every Therefore operation expects. A string body is sent as
    /// JSON text as-is; anything else is serialized to JSON.</summary>
    public static HttpRequestMessage Create(ConnectionDefinition connection, HttpMethod method, string? path, object? body = null)
    {
        if (!connection.IsConfigured || connection.ServiceUrl is not { } serviceUrl)
            throw new InvalidOperationException($"The connection \"{connection.Name}\" isn't fully set up — finish it in Settings ▸ Connections.");

        var request = new HttpRequestMessage(method, Resolve(connection, serviceUrl, path));
        if (body is null && connection.Type == ConnectionType.Therefore && method == HttpMethod.Post)
            body = "{}";
        if (body is not null)
        {
            var json = body as string ?? JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        if (connection.Type == ConnectionType.Therefore)
            request.Headers.TryAddWithoutValidation("TenantName", connection.TenantName?.Trim() ?? string.Empty);

        switch (connection.AuthMethod)
        {
            case ConnectionAuthMethod.Basic:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}")));
                break;
            case ConnectionAuthMethod.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
                break;
            case ConnectionAuthMethod.ApiKey:
                request.Headers.TryAddWithoutValidation(connection.ApiKeyHeader!.Trim(), connection.Token);
                break;
        }

        return request;
    }

    private static Uri Resolve(ConnectionDefinition connection, string serviceUrl, string? path)
    {
        var baseUri = new Uri(serviceUrl.TrimEnd('/') + "/");
        var target = Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute
            : new Uri(baseUri, (path ?? string.Empty).TrimStart('/'));

        if (!string.Equals(target.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The connection \"{connection.Name}\" only sends requests to {baseUri.GetLeftPart(UriPartial.Authority)}, not {target.GetLeftPart(UriPartial.Authority)}.");
        }

        return target;
    }

    /// <summary>Checks a connection signs in: Therefore asks for a connection token; REST requests the
    /// base address, where any answer other than a sign-in refusal or server error counts as reached.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(HttpClient http, ConnectionDefinition connection, CancellationToken cancellationToken = default)
    {
        try
        {
            var isTherefore = connection.Type == ConnectionType.Therefore;
            using var request = Create(connection, isTherefore ? HttpMethod.Post : HttpMethod.Get, isTherefore ? "GetConnectionToken" : string.Empty);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (isTherefore)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return (false, $"Therefore returned {status}: {Shorten(text)}");
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                return json.RootElement.TryGetProperty("Token", out var token) && !string.IsNullOrEmpty(token.GetString())
                    ? (true, "Connected to Therefore.")
                    : (false, "Therefore answered, but didn't return a connection token.");
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (false, $"The server refused the sign-in ({status})."),
                _ when status >= 500 => (false, $"The server returned an error ({status})."),
                _ when response.IsSuccessStatusCode => (true, $"Connected ({status})."),
                _ => (true, $"Reached the server ({status} for the base address, which is normal for many APIs).")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or UriFormatException)
        {
            return (false, ex is TaskCanceledException ? "The server didn't answer in time." : ex.Message);
        }
    }

    internal static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
