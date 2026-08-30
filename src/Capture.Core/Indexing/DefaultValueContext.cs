namespace Capture.Core.Indexing;

/// <summary>Ambient values a Text field's <c>DefaultValueTemplate</c> can draw on — the document/batch
/// counters, timestamp, and profile name (fixed for the whole document), plus every other field's
/// already-resolved value keyed by name for <c>{FieldName}</c> references.</summary>
public sealed class DefaultValueContext
{
    public int DocumentNumber { get; init; } = 1;
    public int BatchNumber { get; init; } = 1;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string? ProfileName { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
