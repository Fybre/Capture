using Capture.Core.Models;

namespace Capture.Core.Scripting;

/// <summary>Everything a script gets to see, built by <c>ProfileApplicator</c> once per document (and
/// reused across every script/expression run for that document, so later scripts see earlier ones'
/// mutations). Mirrors <c>DefaultValueContext</c>'s document/batch metadata shape for consistency with
/// the existing Text/Lookup template feature.</summary>
public sealed class ScriptExecutionContext
{
    public required string ProfileName { get; init; }
    public required int DocumentNumber { get; init; }
    public required int BatchNumber { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The real, mutable <see cref="IndexValue"/> instances for the document — a profile-level
    /// script's writes to these are what makes its mutations visible to the rest of the pipeline. Field
    /// expressions never write here; the runner returns their result instead (see
    /// <see cref="IFieldScriptRunner.RunFieldExpressionAsync"/>).</summary>
    public required IReadOnlyList<IndexValue> Values { get; init; }
}
