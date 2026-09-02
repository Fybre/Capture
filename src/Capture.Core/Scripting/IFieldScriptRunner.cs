using Capture.Core.Profiles;

namespace Capture.Core.Scripting;

/// <summary>Compiles and runs the C# scripts attached to an <see cref="IndexingProfile"/> — both
/// profile-level <see cref="FieldScript"/>s (imperative, full read/write over every field) and
/// per-field <see cref="IndexField.ScriptExpression"/>s (a single expression, read-only over every
/// other field, whose result becomes that field's value). Never throws — a script failure always
/// comes back as a failed <see cref="ScriptRunResult"/>, never an exception out of these methods,
/// mirroring the "a step's own failure must never abort import" contract already used by
/// <c>IPostIndexStep</c> and <c>DefaultValueTemplateEvaluator</c>.</summary>
public interface IFieldScriptRunner
{
    /// <summary>False when scripting is turned off in Settings (<c>WatchSettings.AllowFieldScripts</c>)
    /// — callers should skip script execution entirely rather than call Run*Async, exactly like
    /// <c>IAiExtractor.IsConfigured</c> gates AI extraction.</summary>
    bool IsAvailable { get; }

    /// <summary>Runs one profile-level script. <paramref name="context"/>'s <c>Values</c> are the real,
    /// mutable <see cref="Capture.Core.Models.IndexValue"/> instances — a successful run's mutations are
    /// already reflected in them; there is no separate result payload to copy back.</summary>
    Task<ScriptRunResult> RunProfileScriptAsync(
        FieldScript script,
        ScriptExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Evaluates one field's <see cref="IndexField.ScriptExpression"/> against a read-only view
    /// of <paramref name="context"/>'s fields. On success, <see cref="ScriptRunResult.Value"/> holds the
    /// resolved value to assign to that field — the runner never mutates <paramref name="context"/> for
    /// a field expression, since other fields must stay read-only to it.</summary>
    Task<ScriptRunResult> RunFieldExpressionAsync(
        Guid scriptCacheKey,
        string expression,
        ScriptExecutionContext context,
        CancellationToken cancellationToken = default);
}
