using Capture.Core.Models;

namespace Capture.Scripting;

/// <summary>A profile-level script's read/write view of one field. Deliberately narrower than the real
/// <see cref="IndexValue"/> — extraction-internal state (Bounds, PageNumber, LookupOptions, Level, ...)
/// isn't exposed, keeping the script surface small and stable. Setting <see cref="Value"/>/
/// <see cref="Confidence"/> writes straight through to the wrapped <see cref="IndexValue"/> — there is
/// no separate copy-back step.</summary>
public sealed class ScriptFieldAccessor
{
    private readonly IndexValue _value;

    internal ScriptFieldAccessor(IndexValue value)
    {
        _value = value;
    }

    public string Name => _value.FieldName;

    public string Value
    {
        get => _value.Value;
        set => _value.Value = value;
    }

    /// <summary>Informational only — not an enforcement mechanism. A script's write to a field the
    /// indexer already edited manually (Text/Lookup/Script kinds) is silently reverted afterward by
    /// <c>ProfileApplicator.ApplyDefaults</c> regardless of what a script does here; this is exposed so
    /// a well-behaved script can skip pointless work, not to guarantee anything on its own.</summary>
    public bool IsManual => _value.IsManual;

    public float Confidence
    {
        get => _value.Confidence;
        set => _value.Confidence = value;
    }

    public override string ToString() => Value;
}

/// <summary>A field expression's read-only view of every field (including its own, pre-evaluation
/// current value, and every other field) — no setters, which is what structurally prevents a field
/// expression from reaching into and mutating unrelated fields as a side effect.</summary>
public sealed class ReadOnlyScriptFieldAccessor
{
    private readonly IndexValue _value;

    internal ReadOnlyScriptFieldAccessor(IndexValue value)
    {
        _value = value;
    }

    public string Name => _value.FieldName;

    public string Value => _value.Value;

    public bool IsManual => _value.IsManual;

    public float Confidence => _value.Confidence;

    public override string ToString() => Value;
}
