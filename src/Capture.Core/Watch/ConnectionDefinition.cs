using System.Text.Json.Serialization;

namespace Capture.Core.Watch;

public enum ConnectionType
{
    Therefore = 0,
    Rest = 1,

    /// <summary>An Azure AI Document Intelligence resource, used for external OCR: its endpoint and key.</summary>
    AzureDocumentIntelligence = 2
}

public enum ConnectionAuthMethod
{
    None = 0,
    Basic = 1,
    Bearer = 2,

    /// <summary>The <see cref="ConnectionDefinition.Token"/> sent in the header named by
    /// <see cref="ConnectionDefinition.ApiKeyHeader"/>.</summary>
    ApiKey = 3
}

/// <summary>One named connection to an outside system, set up in Settings ▸ Connections. Exports pick
/// one by <see cref="Id"/>; scripts reach one by <see cref="Name"/> via <c>Connections["Name"]</c>, which
/// signs each request without ever handing the script the credentials.</summary>
public sealed class ConnectionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ConnectionType Type { get; set; } = ConnectionType.Rest;

    /// <summary>Therefore: the server address (any level down to <c>/restun</c>); blank builds it from
    /// <see cref="TenantName"/>. REST: the address every script path is relative to.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Therefore only — sent as the <c>TenantName</c> header, exactly as typed.</summary>
    public string? TenantName { get; set; }

    public ConnectionAuthMethod AuthMethod { get; set; } = ConnectionAuthMethod.Basic;
    public string? Username { get; set; }

    /// <summary>Secret — kept in the OS credential store, never in settings.json.</summary>
    public string? Password { get; set; }

    /// <summary>Secret — the bearer token or API key, depending on <see cref="AuthMethod"/>.</summary>
    public string? Token { get; set; }

    public string? ApiKeyHeader { get; set; } = "X-API-Key";

    [JsonIgnore]
    public IReadOnlyList<ConnectionAuthMethod> AuthMethods => AuthMethodsFor(Type);

    /// <summary>The header Azure AI services read their key from.</summary>
    public const string AzureKeyHeader = "Ocp-Apim-Subscription-Key";

    public static IReadOnlyList<ConnectionAuthMethod> AuthMethodsFor(ConnectionType type) => type switch
    {
        ConnectionType.Therefore => [ConnectionAuthMethod.Basic, ConnectionAuthMethod.Bearer],
        ConnectionType.AzureDocumentIntelligence => [ConnectionAuthMethod.ApiKey],
        _ => [ConnectionAuthMethod.None, ConnectionAuthMethod.Basic, ConnectionAuthMethod.Bearer, ConnectionAuthMethod.ApiKey]
    };

    /// <summary>The address requests are made against: Therefore's <c>…/theservice/v0001/restun</c>, or
    /// the REST base address as entered. Null when there isn't a usable address yet.</summary>
    [JsonIgnore]
    public string? ServiceUrl
    {
        get
        {
            if (Type == ConnectionType.Therefore)
            {
                return string.IsNullOrWhiteSpace(BaseUrl) && string.IsNullOrWhiteSpace(TenantName)
                    ? null
                    : ThereforeServiceUrl(TenantName, BaseUrl);
            }

            var url = BaseUrl?.Trim();
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? url : null;
        }
    }

    [JsonIgnore]
    public bool IsConfigured => ServiceUrl is not null && AuthMethods.Contains(AuthMethod) && AuthMethod switch
    {
        ConnectionAuthMethod.None => true,
        ConnectionAuthMethod.Basic => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password),
        ConnectionAuthMethod.Bearer => !string.IsNullOrWhiteSpace(Token),
        // Azure always sends its key in AzureKeyHeader, so it has no header of its own to fill in.
        ConnectionAuthMethod.ApiKey => !string.IsNullOrWhiteSpace(Token)
            && (Type == ConnectionType.AzureDocumentIntelligence || !string.IsNullOrWhiteSpace(ApiKeyHeader)),
        _ => false
    };

    /// <summary>Normalizes a Therefore server address: builds <c>https://{tenant}.thereforeonline.com</c>
    /// when blank, adds a scheme if missing, and accepts an address that already ends in
    /// <c>/theservice</c>, <c>/theservice/v0001</c> or <c>/theservice/v0001/restun</c>.</summary>
    public static string ThereforeServiceUrl(string? tenantName, string? baseUrl)
    {
        var server = (baseUrl ?? string.Empty).Trim();
        if (server.Length == 0)
            server = $"https://{tenantName?.Trim()}.thereforeonline.com";
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
}
