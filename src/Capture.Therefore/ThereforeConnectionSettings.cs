namespace Capture.Therefore;

public enum ThereforeAuthMethod
{
    Basic = 0,
    Bearer = 1
}

/// <summary>What <see cref="ThereforeClient"/> needs to reach a server, built at call time from a
/// Therefore <see cref="Capture.Core.Watch.ConnectionDefinition"/> — never persisted as its own object.</summary>
public sealed class ThereforeConnectionSettings
{
    public required string BaseUrl { get; init; }

    /// <summary>Sent as the <c>TenantName</c> header on every request, exactly as typed (including
    /// empty for an on-premise server) — matches the working reference client's behavior rather than
    /// trying to infer it from <see cref="BaseUrl"/>'s host.</summary>
    public string? TenantName { get; init; }

    public ThereforeAuthMethod AuthMethod { get; init; } = ThereforeAuthMethod.Basic;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? BearerToken { get; init; }

    public static ThereforeConnectionSettings From(Capture.Core.Watch.ConnectionDefinition connection) => new()
    {
        BaseUrl = connection.BaseUrl ?? string.Empty,
        TenantName = string.IsNullOrWhiteSpace(connection.TenantName) ? null : connection.TenantName.Trim(),
        AuthMethod = connection.AuthMethod == Capture.Core.Watch.ConnectionAuthMethod.Bearer ? ThereforeAuthMethod.Bearer : ThereforeAuthMethod.Basic,
        Username = connection.Username,
        Password = connection.Password,
        BearerToken = connection.Token
    };
}
