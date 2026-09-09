using Capture.Core.Scripting;

namespace Capture.Scripting;

/// <summary>The Roslyn <c>globalsType</c> for a profile-level script — mutable, full read/write access
/// over every field. Instance members are visible as bare identifiers inside script text (Roslyn
/// scripting convention), so a script just writes <c>Fields["InvoiceNo"].Value = "123";</c>.</summary>
public sealed class ScriptGlobals
{
    internal ScriptGlobals(ScriptExecutionContext context, HttpClient http, string scriptName, CancellationToken cancellationToken)
    {
        Fields = new ScriptFieldCollection<ScriptFieldAccessor>(context.Values, v => new ScriptFieldAccessor(v));
        Context = ScriptContextView.Create(context, Fields,
            context.BatchValues is null ? null : new ScriptFieldCollection<ScriptFieldAccessor>(context.BatchValues, v => new ScriptFieldAccessor(v)));
        Document = context.Document;
        ProfileName = context.ProfileName;
        DocumentNumber = context.DocumentNumber;
        BatchNumber = context.BatchNumber;
        Timestamp = context.Timestamp;
        Http = http;
        Log = new ScriptLog(scriptName);
        CancellationToken = cancellationToken;
    }

    public ScriptFieldCollection<ScriptFieldAccessor> Fields { get; }
    public ScriptContextView Context { get; }

    public ScriptDocumentInfo Document { get; }

    public string ProfileName { get; }

    public int DocumentNumber { get; }

    public int BatchNumber { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>One long-lived, host-owned client (not a fresh one per script run) — real
    /// <c>await Http.GetAsync(...)</c> works out of the box, with a sane shared timeout.</summary>
    public HttpClient Http { get; }

    public ScriptLog Log { get; }

    public CancellationToken CancellationToken { get; }
}

/// <summary>The Roslyn <c>globalsType</c> for a per-field <c>ScriptExpression</c> — read-only over every
/// field (including the field's own pre-evaluation value). No setters anywhere on this type; that
/// absence is what structurally prevents a field expression from mutating unrelated fields, not just a
/// documented convention.</summary>
public sealed class ReadOnlyScriptGlobals
{
    private float? _requestedConfidence;

    internal ReadOnlyScriptGlobals(ScriptExecutionContext context, HttpClient http, string scriptName, string selfValue, CancellationToken cancellationToken)
    {
        Fields = new ScriptFieldCollection<ReadOnlyScriptFieldAccessor>(context.Values, v => new ReadOnlyScriptFieldAccessor(v));
        Context = ReadOnlyScriptContextView.Create(context, Fields,
            context.BatchValues is null ? null : new ScriptFieldCollection<ReadOnlyScriptFieldAccessor>(context.BatchValues, v => new ReadOnlyScriptFieldAccessor(v)));
        Document = context.Document;
        ProfileName = context.ProfileName;
        DocumentNumber = context.DocumentNumber;
        BatchNumber = context.BatchNumber;
        Timestamp = context.Timestamp;
        Http = http;
        Log = new ScriptLog(scriptName);
        Value = selfValue;
        CancellationToken = cancellationToken;
    }

    public ScriptFieldCollection<ReadOnlyScriptFieldAccessor> Fields { get; }
    public ReadOnlyScriptContextView Context { get; }

    /// <summary>Shorthand for this field's own pre-evaluation value — identical to looking it up via
    /// <c>Fields["ThisField'sOwnName"]</c>.Value, without needing to spell out (and keep in sync with)
    /// the field's own name. Roslyn scripting resolves globals members as bare identifiers, not through
    /// <c>this</c> (the script's compiler-generated class doesn't inherit from the globals type), so
    /// scripts reference it as plain <c>Value</c>, the same way they already use <c>Document</c> or
    /// <c>Http</c>.</summary>
    public string Value { get; }

    /// <summary>Sets the confidence of this expression's own output. Other fields remain read-only.</summary>
    public void SetConfidence(double confidence)
    {
        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0 || confidence > 100)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0 and 100.");
        _requestedConfidence = (float)confidence;
    }

    internal float? RequestedConfidence => _requestedConfidence;

    public ScriptDocumentInfo Document { get; }

    public string ProfileName { get; }

    public int DocumentNumber { get; }

    public int BatchNumber { get; }

    public DateTimeOffset Timestamp { get; }

    public HttpClient Http { get; }

    public ScriptLog Log { get; }

    public CancellationToken CancellationToken { get; }
}

public sealed record ScriptTriggerMatchView(Guid RuleId, string? CapturedValue, double? Confidence);
public sealed record ScriptTriggerView(IReadOnlyList<ScriptTriggerMatchView> Matches);

public sealed class ScriptScopeView<TField> where TField : class
{
    internal ScriptScopeView(int number, string? type, ScriptFieldCollection<TField> fields, string text)
    { Number = number; Type = type; Fields = fields; Text = text; }
    public int Number { get; }
    public string? Type { get; }
    public ScriptFieldCollection<TField> Fields { get; }
    public string Text { get; }
}

public sealed class ScriptContextView
{
    private ScriptContextView(ScriptScopeView<ScriptFieldAccessor>? batch, ScriptScopeView<ScriptFieldAccessor>? document, ScriptTriggerView trigger)
    { Batch = batch; Document = document; Trigger = trigger; }
    public ScriptScopeView<ScriptFieldAccessor>? Batch { get; }
    public ScriptScopeView<ScriptFieldAccessor>? Document { get; }
    public ScriptTriggerView Trigger { get; }
    internal static ScriptContextView Create(ScriptExecutionContext context, ScriptFieldCollection<ScriptFieldAccessor> fields, ScriptFieldCollection<ScriptFieldAccessor>? batchFields) => new(
        context.Scope == ScriptScopeKind.Batch ? new(context.BatchNumber, null, fields, string.Empty) : batchFields is null ? null : new(context.BatchNumber, null, batchFields, string.Empty),
        context.Scope == ScriptScopeKind.Document ? new(context.DocumentNumber, context.DocumentType, fields, context.Document.Text) : null,
        CreateTrigger(context));
    private static ScriptTriggerView CreateTrigger(ScriptExecutionContext context) => new(context.TriggerMatches.Select(match => new ScriptTriggerMatchView(match.RuleId, match.CapturedValue, match.Confidence)).ToList());
}

public sealed class ReadOnlyScriptContextView
{
    private ReadOnlyScriptContextView(ScriptScopeView<ReadOnlyScriptFieldAccessor>? batch, ScriptScopeView<ReadOnlyScriptFieldAccessor>? document, ScriptTriggerView trigger)
    { Batch = batch; Document = document; Trigger = trigger; }
    public ScriptScopeView<ReadOnlyScriptFieldAccessor>? Batch { get; }
    public ScriptScopeView<ReadOnlyScriptFieldAccessor>? Document { get; }
    public ScriptTriggerView Trigger { get; }
    internal static ReadOnlyScriptContextView Create(ScriptExecutionContext context, ScriptFieldCollection<ReadOnlyScriptFieldAccessor> fields, ScriptFieldCollection<ReadOnlyScriptFieldAccessor>? batchFields) => new(
        context.Scope == ScriptScopeKind.Batch ? new(context.BatchNumber, null, fields, string.Empty) : batchFields is null ? null : new(context.BatchNumber, null, batchFields, string.Empty),
        context.Scope == ScriptScopeKind.Document ? new(context.DocumentNumber, context.DocumentType, fields, context.Document.Text) : null,
        new ScriptTriggerView(context.TriggerMatches.Select(match => new ScriptTriggerMatchView(match.RuleId, match.CapturedValue, match.Confidence)).ToList()));
}
