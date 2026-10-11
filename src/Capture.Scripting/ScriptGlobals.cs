using Capture.Core.Scripting;

namespace Capture.Scripting;

/// <summary>The Roslyn <c>globalsType</c> for a profile-level script — mutable, full read/write access
/// over every field. Instance members are visible as bare identifiers inside script text (Roslyn
/// scripting convention), so a script just writes <c>Fields["InvoiceNo"].Value = "123";</c>.</summary>
public sealed class ScriptGlobals
{
    private readonly IScriptDialogs? _dialogs;
    private readonly ScriptTimeout? _timeout;
    private readonly string _scriptName;

    internal ScriptGlobals(ScriptExecutionContext context, HttpClient http, ScriptConnections connections, string scriptName, CancellationToken cancellationToken, ScriptTimeout? timeout = null)
    {
        _dialogs = context.Dialogs;
        _timeout = timeout;
        _scriptName = scriptName;
        Connections = connections;
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

    /// <summary>Settings ▸ Connections by name — <c>await Connections["Xero"].GetAsync("...")</c> sends a
    /// request signed with that connection's stored credentials, which the script never sees.</summary>
    public ScriptConnections Connections { get; }

    public ScriptLog Log { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>Shows a message with an OK button and waits until it's closed. Button field scripts only —
    /// the title defaults to the button field's name.</summary>
    public async Task Message(string message, string? title = null)
    {
        var dialogs = RequireDialogs(nameof(Message));
        await Paused(async () =>
        {
            await dialogs.MessageAsync(title ?? _scriptName, message).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>Asks a Yes/No question and returns true for Yes. Button field scripts only — the title
    /// defaults to the button field's name.</summary>
    public Task<bool> Confirm(string question, string? title = null)
    {
        var dialogs = RequireDialogs(nameof(Confirm));
        return Paused(() => dialogs.ConfirmAsync(title ?? _scriptName, question));
    }

    private IScriptDialogs RequireDialogs(string name) => _dialogs
        ?? throw new InvalidOperationException($"{name}() only works in a Button field's script — no one is there to answer it during an import or export.");

    private Task<T> Paused<T>(Func<Task<T>> wait) => _timeout is null ? wait() : _timeout.PausedAsync(wait);
}

/// <summary>The Roslyn <c>globalsType</c> for a per-field <c>ScriptExpression</c> — read-only over every
/// field (including the field's own pre-evaluation value). No setters anywhere on this type; that
/// absence is what structurally prevents a field expression from mutating unrelated fields, not just a
/// documented convention.</summary>
public sealed class ReadOnlyScriptGlobals
{
    private float? _requestedConfidence;

    internal ReadOnlyScriptGlobals(ScriptExecutionContext context, HttpClient http, ScriptConnections connections, string scriptName, string selfValue, CancellationToken cancellationToken)
    {
        Connections = connections;
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

    /// <summary>Settings ▸ Connections by name — <c>await Connections["Xero"].GetAsync("...")</c> sends a
    /// request signed with that connection's stored credentials, which the script never sees.</summary>
    public ScriptConnections Connections { get; }

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
