using System.Text.Json;
using Capture.Core.Watch;

namespace Capture.Scripting;

/// <summary>The script global <c>Connections</c>: the connections set up in Settings ▸ Connections, by
/// name. A script gets a <see cref="ScriptConnection"/> that signs its requests — never the credentials.</summary>
public sealed class ScriptConnections
{
    private readonly Lazy<IReadOnlyList<ConnectionDefinition>> _connections;
    private readonly HttpClient _http;
    private readonly CancellationToken _cancellationToken;

    internal ScriptConnections(Func<IReadOnlyList<ConnectionDefinition>> load, HttpClient http, CancellationToken cancellationToken)
    {
        _connections = new Lazy<IReadOnlyList<ConnectionDefinition>>(load);
        _http = http;
        _cancellationToken = cancellationToken;
    }

    public ScriptConnection this[string name] => TryGet(name, out var connection)
        ? connection
        : throw new KeyNotFoundException($"No connection named \"{name}\" — add it in Settings ▸ Connections.");

    public bool TryGet(string name, out ScriptConnection connection)
    {
        var match = _connections.Value.FirstOrDefault(item => string.Equals(item.Name.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase));
        connection = match is null ? null! : new ScriptConnection(match, _http, _cancellationToken);
        return match is not null;
    }

    public IReadOnlyList<string> Names => _connections.Value.Select(item => item.Name).ToList();
}

/// <summary>One connection as a script sees it. Paths are relative to the connection's address — for
/// Therefore that's <c>…/theservice/v0001/restun</c>, so <c>PostAsync("ExecuteSingleQuery", body)</c>.
/// A non-2xx answer is returned, not thrown; call <see cref="ConnectionResponse.EnsureSuccess"/> when
/// it should stop the script.</summary>
public sealed class ScriptConnection
{
    private readonly ConnectionDefinition _definition;
    private readonly HttpClient _http;
    private readonly CancellationToken _cancellationToken;

    internal ScriptConnection(ConnectionDefinition definition, HttpClient http, CancellationToken cancellationToken)
    {
        _definition = definition;
        _http = http;
        _cancellationToken = cancellationToken;
    }

    public string Name => _definition.Name;

    /// <summary>"Therefore" or "Rest".</summary>
    public string Type => _definition.Type.ToString();

    public bool IsConfigured => _definition.IsConfigured;

    public Task<ConnectionResponse> GetAsync(string path) => SendAsync("GET", path);

    public Task<ConnectionResponse> PostAsync(string path, object? body = null) => SendAsync("POST", path, body);

    public Task<ConnectionResponse> PutAsync(string path, object? body = null) => SendAsync("PUT", path, body);

    public Task<ConnectionResponse> PatchAsync(string path, object? body = null) => SendAsync("PATCH", path, body);

    public Task<ConnectionResponse> DeleteAsync(string path) => SendAsync("DELETE", path);

    public async Task<ConnectionResponse> SendAsync(string method, string path, object? body = null)
    {
        using var request = ConnectionRequests.Create(_definition, new HttpMethod(method.ToUpperInvariant()), path, body);
        try
        {
            using var response = await _http.SendAsync(request, _cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(_cancellationToken).ConfigureAwait(false);
            return new ConnectionResponse(Name, (int)response.StatusCode, text);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't reach \"{Name}\": {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!_cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"\"{Name}\" didn't answer in time.", ex);
        }
    }
}

public sealed class ConnectionResponse
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _connectionName;
    private JsonDocument? _json;

    internal ConnectionResponse(string connectionName, int statusCode, string text)
    {
        _connectionName = connectionName;
        StatusCode = statusCode;
        Text = text;
    }

    public int StatusCode { get; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string Text { get; }

    /// <summary>The body parsed as JSON.</summary>
    public JsonElement Json
    {
        get
        {
            try
            {
                _json ??= JsonDocument.Parse(string.IsNullOrWhiteSpace(Text) ? "null" : Text);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"\"{_connectionName}\" didn't return JSON ({StatusCode}): {Shorten(Text)}");
            }

            return _json.RootElement;
        }
    }

    /// <summary>The body deserialized into <typeparamref name="T"/>, matching property names in any case.</summary>
    public T As<T>()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Text, ReadOptions)!;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"\"{_connectionName}\" returned something that doesn't fit {typeof(T).Name}: {ex.Message}");
        }
    }

    public ConnectionResponse EnsureSuccess() => IsSuccess
        ? this
        : throw new InvalidOperationException($"\"{_connectionName}\" returned {StatusCode}: {Shorten(Text)}");

    public override string ToString() => Text;

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
