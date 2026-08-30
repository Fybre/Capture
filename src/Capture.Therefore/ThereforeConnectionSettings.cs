namespace Capture.Therefore;

public enum ThereforeAuthMethod
{
    Basic = 0,
    Bearer = 1
}

/// <summary>The one shared Therefore server connection (see <c>WatchSettings</c>'s Therefore* fields,
/// which this is built from at call time — never persisted as its own object).</summary>
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
}
