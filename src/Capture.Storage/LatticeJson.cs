using System.Collections.Concurrent;
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

    // Shared across every JSON-backed store in this assembly, keyed by the destination path, so two
    // writes to the same file (e.g. an index-value save racing an import) always serialize instead of
    // corrupting or truncating each other.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    /// <summary>
    /// Atomic, per-path-serialized JSON write: serializes to a temp file, then renames it into place.
    /// A reader (or a crash mid-write) never observes a partially-written file, and concurrent writers
    /// to the same path are queued rather than interleaving into it.
    /// </summary>
    public static async Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tempPath = path + ".tmp";
            await using (var stream = File.Create(tempPath))
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public static Task WriteAsync(string path, PageLattice lattice, CancellationToken cancellationToken = default) =>
        WriteJsonAsync(path, lattice, Options, cancellationToken);

    public static async Task<PageLattice?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PageLattice>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }
}
