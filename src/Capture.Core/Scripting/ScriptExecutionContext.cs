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

    /// <summary>Document-level facts (file name/extension, page count, full extracted text) — read-only
    /// regardless of whether the running script itself is a mutable profile-level script or a read-only
    /// field expression, since there's nothing meaningful to write back to it.</summary>
    public required ScriptDocumentInfo Document { get; init; }
    public ScriptScopeKind Scope { get; init; } = ScriptScopeKind.Document;
    public string? DocumentType { get; init; }
    public IReadOnlyList<ScriptTriggerMatchInfo> TriggerMatches { get; init; } = [];
    public IReadOnlyList<IndexValue>? BatchValues { get; init; }

    /// <summary>Set only when someone is there to answer — a Button field's script run from review or
    /// the designer's Run test. Null during imports and exports, where <c>Message</c>/<c>Confirm</c> fail
    /// instead of waiting on a dialog nobody will see.</summary>
    public IScriptDialogs? Dialogs { get; init; }
}

/// <summary>Shows a script's <c>Message</c> and <c>Confirm</c> dialogs.</summary>
public interface IScriptDialogs
{
    Task MessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string question);
}

public enum ScriptScopeKind { Batch, Document }
public sealed record ScriptTriggerMatchInfo(Guid RuleId, string? CapturedValue, double? Confidence);
