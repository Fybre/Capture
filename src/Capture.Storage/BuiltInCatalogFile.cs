using System.Diagnostics;
using System.Text.Json;

namespace Capture.Storage;

/// <summary>A user-editable JSON catalog seeded from built-in entries. Written from the built-ins on first
/// run; afterwards the file is the user's, except that built-ins added in a newer version are added to it
/// once. A sidecar file (<c>*.offered.json</c>) records which built-ins have been offered, so a deleted
/// entry stays deleted, and a catalog with no built-in entries left (replaced with the user's own) is
/// never refilled. Edited entries are left as they are.</summary>
internal sealed class BuiltInCatalogFile<T>(
    string path,
    IReadOnlyList<T> builtIns,
    Func<T, string> id,
    Func<T, string> group,
    JsonSerializerOptions options,
    IReadOnlyCollection<string>? addedWithOfferTracking = null)
{
    private string OfferedPath => Path.ChangeExtension(path, ".offered.json");

    public async Task<IReadOnlyList<T>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            await WriteAsync(builtIns, cancellationToken).ConfigureAwait(false);
            await WriteOfferedAsync(builtIns.Select(id), cancellationToken).ConfigureAwait(false);
            return builtIns;
        }

        List<T>? items;
        try
        {
            await using var stream = File.OpenRead(path);
            items = await JsonSerializer.DeserializeAsync<List<T>>(stream, options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            Trace.TraceError($"Failed to parse '{path}': {ex.Message}. Using built-in defaults.");
            return builtIns;
        }

        if (items is not { Count: > 0 })
            return builtIns;

        var offered = await ReadOfferedAsync(cancellationToken).ConfigureAwait(false);
        if (AddNewBuiltIns(items, builtIns, offered, id, group))
            await WriteAsync(items, cancellationToken).ConfigureAwait(false);
        if (!builtIns.All(item => offered.Contains(id(item))))
            await WriteOfferedAsync(offered.Union(builtIns.Select(id)), cancellationToken).ConfigureAwait(false);
        return items;
    }

    /// <summary>Adds each built-in not yet offered, after the last entry in its group (or at the end) —
    /// only when the catalog still has at least one built-in entry.</summary>
    internal static bool AddNewBuiltIns(
        List<T> items, IReadOnlyList<T> builtIns, IReadOnlySet<string> offered, Func<T, string> id, Func<T, string> group)
    {
        var builtInIds = builtIns.Select(id).ToHashSet(StringComparer.Ordinal);
        var ids = items.Select(id).ToHashSet(StringComparer.Ordinal);
        if (!ids.Overlaps(builtInIds))
            return false;

        var added = false;
        foreach (var builtIn in builtIns.Where(item => !ids.Contains(id(item)) && !offered.Contains(id(item))))
        {
            var last = items.FindLastIndex(item => string.Equals(group(item), group(builtIn), StringComparison.Ordinal));
            items.Insert(last < 0 ? items.Count : last + 1, builtIn);
            added = true;
        }

        return added;
    }

    // Absent for a catalog written before offers were tracked: every built-in except those added
    // alongside the tracking counts as already offered.
    private async Task<HashSet<string>> ReadOfferedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(OfferedPath))
            {
                await using var stream = File.OpenRead(OfferedPath);
                var ids = await JsonSerializer.DeserializeAsync<List<string>>(stream, options, cancellationToken).ConfigureAwait(false);
                if (ids is not null)
                    return ids.ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (JsonException ex)
        {
            Trace.TraceError($"Failed to parse '{OfferedPath}': {ex.Message}.");
        }

        return builtIns.Select(id).Except(addedWithOfferTracking ?? []).ToHashSet(StringComparer.Ordinal);
    }

    private Task WriteOfferedAsync(IEnumerable<string> ids, CancellationToken cancellationToken) =>
        LatticeJson.WriteJsonAsync(OfferedPath, ids.Order(StringComparer.Ordinal).ToList(), options, cancellationToken);

    private Task WriteAsync(IReadOnlyList<T> items, CancellationToken cancellationToken) =>
        LatticeJson.WriteJsonAsync(path, items, options, cancellationToken);
}
