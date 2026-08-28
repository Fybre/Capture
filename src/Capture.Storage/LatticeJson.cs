using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Core.Lattice;

namespace Capture.Storage;

public static class LatticeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task WriteAsync(string path, PageLattice lattice, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, lattice, Options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PageLattice?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PageLattice>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }
}
