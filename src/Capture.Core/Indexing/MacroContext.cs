namespace Capture.Core.Indexing;

public sealed class MacroContext
{
    public int DocumentNumber { get; init; } = 1;
    public int BatchNumber { get; init; } = 1;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string? ProfileName { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
