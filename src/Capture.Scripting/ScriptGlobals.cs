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
    internal ReadOnlyScriptGlobals(ScriptExecutionContext context, HttpClient http, string scriptName, string selfValue, CancellationToken cancellationToken)
    {
        Fields = new ScriptFieldCollection<ReadOnlyScriptFieldAccessor>(context.Values, v => new ReadOnlyScriptFieldAccessor(v));
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

    /// <summary>Shorthand for this field's own pre-evaluation value — identical to looking it up via
    /// <c>Fields["ThisField'sOwnName"]</c>.Value, without needing to spell out (and keep in sync with)
    /// the field's own name. Roslyn scripting resolves globals members as bare identifiers, not through
    /// <c>this</c> (the script's compiler-generated class doesn't inherit from the globals type), so
    /// scripts reference it as plain <c>Value</c>, the same way they already use <c>Document</c> or
    /// <c>Http</c>.</summary>
    public string Value { get; }

    public ScriptDocumentInfo Document { get; }

    public string ProfileName { get; }

    public int DocumentNumber { get; }

    public int BatchNumber { get; }

    public DateTimeOffset Timestamp { get; }

    public HttpClient Http { get; }

    public ScriptLog Log { get; }

    public CancellationToken CancellationToken { get; }
}
