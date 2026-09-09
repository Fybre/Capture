namespace Capture.Core.Indexing;

using Capture.Core.Scripting;

/// <summary>Ambient values a field's <c>DefaultValueTemplate</c> can draw on — document, batch, and
/// page counters, timestamp, profile name, plus other resolved index values keyed by field name.</summary>
public sealed class DefaultValueContext
{
    public int DocumentNumber { get; init; } = 1;
    public int BatchNumber { get; init; } = 1;
    public int PageNumber { get; init; } = 1;
    public int PageCount { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string? ProfileName { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public ScriptScopeKind? ScriptScope { get; init; }
    public string? DocumentType { get; init; }
    public IReadOnlyList<ScriptTriggerMatchInfo> TriggerMatches { get; init; } = [];
    public IReadOnlyList<Capture.Core.Models.IndexValue>? BatchValues { get; init; }
}
