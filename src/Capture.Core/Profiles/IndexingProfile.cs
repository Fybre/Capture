namespace Capture.Core.Profiles;

public enum FieldKind
{
    Zonal = 0,
    KeyValue = 1,
    Regex = 2,

    // 3 (Macro) retired — a Text field's DefaultValueTemplate now covers the same ground (computed
    // {Token} defaults) without needing a separate field kind.
    Barcode = 4,
    Ai = 5,

    /// <summary>Mirrors whatever value the batch profile's trigger (barcode/regex) captured for this
    /// document, if any — supplied ambiently at import time, no zone/pattern configured on the field itself.</summary>
    BatchSeparatorValue = 6,

    /// <summary>A value entered manually in the indexing panel — optionally seeded from
    /// <see cref="IndexField.DefaultValueTemplate"/>.</summary>
    Text = 7,

    /// <summary>A value chosen manually from the field's configured list of display/export pairs.</summary>
    Lookup = 8,

    /// <summary>Computed by evaluating <see cref="IndexField.ScriptExpression"/> (C# via Roslyn
    /// scripting) once fields have been extracted — read-only access to every other field's resolved
    /// value, own value = the expression's result. Requires <c>WatchSettings.AllowFieldScripts</c>.</summary>
    Script = 9,

    /// <summary>Renders as a button in the review panel instead of a value control. Running its
    /// <see cref="IndexField.ButtonScriptSource"/> — full read/write over every field, the same shape as
    /// a profile-level <see cref="FieldScript"/> — is the only way this field's value ever changes; it
    /// is never extracted or computed automatically. Requires <c>WatchSettings.AllowFieldScripts</c>.</summary>
    Button = 10
}

public enum ScriptTrigger
{
    /// <summary>Runs once per document, after every field (including AI and Script kinds) has been
    /// extracted/resolved, before manual-edit preservation and Text/Lookup default templates are
    /// applied. A profile-level script's writes to <c>IndexValue.Value</c> here are real and persisted.</summary>
    AfterFieldsPopulated = 0,

    /// <summary>Runs once per export attempt (<c>ProfileExportRunner.RunAsync</c>), before any writer
    /// runs, over a snapshot of the document's field values — mutations here reshape only what gets
    /// written out, never the stored/reviewed document.</summary>
    BeforeExport = 1,

    /// <summary>Runs once per export attempt after every writer has finished, given each writer's
    /// result — side effects only (e.g. a webhook), field values are not writable at this point.</summary>
    AfterExport = 2
}

/// <summary>A named, independently enable/disable-able C# script attached to a profile. See
/// <see cref="ScriptTrigger"/> for when it runs and <c>IFieldScriptRunner</c> for how.</summary>
public sealed class FieldScript
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New script";
    public bool Enabled { get; set; } = true;
    public ScriptTrigger Trigger { get; set; } = ScriptTrigger.AfterFieldsPopulated;
    public string Source { get; set; } = string.Empty;

    /// <summary>Hard ceiling on one run of this script. .NET's cooperative cancellation can't preempt a
    /// tight non-<c>await</c> loop, so this is a best-effort guard, not an ironclad one — see
    /// <c>RoslynFieldScriptRunner</c>.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

public enum IndexLevel
{
    Document = 0,
    Batch = 1
}

public enum MatchOccurrence
{
    First = 0,
    Last = 1
}

public enum PageScope
{
    First = 0,
    Number = 1,
    Any = 2
}

public enum FieldFormat
{
    String = 0,
    Integer = 1,
    Money = 2,
    Date = 3,
    DateTime = 4,
    Boolean = 5
}

public sealed class ZoneRect
{
    public int PageNumber { get; set; } = 1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public sealed class LookupOption
{
    /// <summary>The human-readable label shown in the indexing panel.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The underlying value stored on the index and supplied to exporters.</summary>
    public string Value { get; set; } = string.Empty;
}

public sealed class IndexField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public FieldKind Kind { get; set; } = FieldKind.Zonal;
    public FieldFormat Format { get; set; } = FieldFormat.String;
    public IndexLevel Level { get; set; } = IndexLevel.Document;
    public bool Mandatory { get; set; }
    public int PageNumber { get; set; } = 1;
    public ZoneRect? Zone { get; set; }
    public string? KeyPattern { get; set; }
    public string? ValuePattern { get; set; }
    public MatchOccurrence Occurrence { get; set; } = MatchOccurrence.First;
    public PageScope PageScope { get; set; } = PageScope.First;

    /// <summary>True once <see cref="PageScope"/> has been deliberately set for this field (via the field
    /// editor). Only meaningful for Barcode fields, whose PageScope was inert dead data before it became
    /// a real, chosen setting — lets JsonProfileStore's migration tell "never touched, still First by
    /// unrelated default" apart from a genuine choice of First, without silently reverting the latter.</summary>
    public bool PageScopeConfigured { get; set; }

    public ZoneRect? SearchZone { get; set; }

    /// <summary>Only meaningful for <see cref="FieldKind.Text"/>. A template evaluated once per
    /// document/batch by <c>DefaultValueTemplateEvaluator</c> — plain text passes through unchanged,
    /// and <c>{Doc#}</c>/<c>{Batch#}</c>/<c>{Date}</c>/<c>{Time}</c>/<c>{ProfileName}</c>/<c>{OtherField}</c>
    /// tokens are resolved. Null/empty means no default — the field starts blank exactly as before.</summary>
    public string? DefaultValueTemplate { get; set; }

    public bool HideFromIndexing { get; set; }

    /// <summary>When true, this field can't be hand-edited in the review panel, and (like
    /// <see cref="HideFromIndexing"/>) is excluded from Mandatory/Ready checks.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>When true, this field's extracted value (its Zone/pattern-matched Bounds) is a redaction
    /// candidate on documents whose profile has <see cref="IndexingProfile.Redaction"/> enabled.</summary>
    public bool Sensitive { get; set; }

    public string? BarcodeFormat { get; set; }
    public string? AiTypeId { get; set; }
    public string? AiPrompt { get; set; }
    public List<LookupOption> LookupOptions { get; set; } = [];

    /// <summary>The export value of the lookup option selected by default, or null for no default.</summary>
    public string? LookupDefaultValue { get; set; }

    /// <summary>Only meaningful for <see cref="FieldKind.Lookup"/>. A template evaluated the same way
    /// as a Text field's <see cref="DefaultValueTemplate"/> (so it can reference another field via
    /// <c>{OtherField}</c>) — the resolved text is matched case-insensitively against this field's
    /// <see cref="LookupOptions"/> keys, and the matching option's value becomes this field's default.
    /// Takes precedence over <see cref="LookupDefaultValue"/> when it resolves to a match on this
    /// document; when it doesn't (no match, or the referenced field is blank), <see cref="LookupDefaultValue"/>
    /// is used instead, if set.</summary>
    public string? LookupKeyTemplate { get; set; }

    /// <summary>Only meaningful for <see cref="FieldKind.Script"/>. C# evaluated via Roslyn scripting
    /// once every other field has been resolved — the expression's result becomes this field's value.
    /// Other fields are read-only to it (unlike a profile-level <see cref="FieldScript"/>, which can
    /// write any field); this is deliberate so a field expression can't reach into and mutate unrelated
    /// fields as a side effect. Requires <c>WatchSettings.AllowFieldScripts</c>.</summary>
    public string? ScriptExpression { get; set; }

    /// <summary>Only meaningful for <see cref="FieldKind.Button"/>. The button's text; falls back to
    /// <see cref="Name"/> when blank.</summary>
    public string? ButtonLabel { get; set; }

    /// <summary>Only meaningful for <see cref="FieldKind.Button"/>. Imperative C#, run only when the
    /// button is clicked — same execution shape as a profile-level <see cref="FieldScript"/> (full
    /// read/write over every field, an <c>Http</c> client, <c>Document</c>), unlike
    /// <see cref="ScriptExpression"/>, which is read-only over other fields. Requires
    /// <c>WatchSettings.AllowFieldScripts</c>.</summary>
    public string ButtonScriptSource { get; set; } = string.Empty;

    /// <summary>Only meaningful for <see cref="FieldKind.Button"/>. Mirrors <see cref="FieldScript.TimeoutSeconds"/>.</summary>
    public int ButtonTimeoutSeconds { get; set; } = 10;

    /// <summary>Available on any field kind except <see cref="FieldKind.Script"/>,
    /// <see cref="FieldKind.Button"/>, and <see cref="FieldKind.BatchSeparatorValue"/>. C# evaluated via
    /// Roslyn once this field's own value has already been extracted/computed by whatever its
    /// <see cref="Kind"/> normally does (zone OCR text, a key/value or regex match, an AI result, a
    /// barcode, ...) — the expression's result replaces that value, so it's the place for cleanup a
    /// field's own extraction mechanism can't express (stripping OCR noise, normalizing whitespace,
    /// re-casing). Same read-only-over-every-field shape as <see cref="ScriptExpression"/> (this field's
    /// own pre-cleanup value is readable the same way, via <c>Fields["ThisField'sName"].Value</c>);
    /// distinct from it in that it runs as a second pass after normal extraction rather than being the
    /// only source of the field's value. Skipped for a field the reviewer has manually edited, same as
    /// every other automatic pipeline step. Requires <c>WatchSettings.AllowFieldScripts</c>.</summary>
    public string? PostProcessScript { get; set; }
}

/// <summary>Profile-level redaction configuration — PII detected by the bundled Presidio sidecar and/or
/// any field marked <see cref="IndexField.Sensitive"/> is offered for redaction once a document reaches
/// <see cref="Capture.Core.Models.DocumentStatus.Ready"/>. See <c>RedactionDetectionStep</c>.</summary>
public sealed class RedactionSettings
{
    public bool Enabled { get; set; }

    /// <summary>Whether Presidio-based PII detection runs at all when <see cref="Enabled"/> is true.
    /// Sensitive-marked fields are redacted regardless of this setting — see
    /// <c>RedactionDetectionStep</c>, which builds their candidates directly from the field's own
    /// extracted bounds without going through Presidio.</summary>
    public bool DetectPii { get; set; } = true;

    /// <summary>Which redaction set (see Capture.Core.Redaction.RedactionEntitySet /
    /// BuiltInRedactionSets) the profile designer's UI has selected — the source of truth for what to
    /// show as "currently chosen" when the profile is reopened. Null for profiles saved before this
    /// existed, or if a custom set the profile referenced was since deleted.</summary>
    public Guid? EntitySetId { get; set; }

    /// <summary>The actual Presidio entity type codes to detect — a snapshot of the chosen set's
    /// entities at the time it was selected/saved, so detection never needs to resolve
    /// <see cref="EntitySetId"/> back through the sets store. Empty/null means Presidio's full default
    /// set for <see cref="Language"/>.</summary>
    public List<string> Entities { get; set; } = [];

    /// <summary>A Presidio match scoring below this is discarded entirely — never becomes a candidate.</summary>
    public int ScoreThresholdPercent { get; set; } = 50;

    /// <summary>A candidate (from either source) scoring at or above this skips manual review and is
    /// redacted automatically. 0 bypasses review unconditionally; the default 100 means only
    /// Sensitive-field candidates (hardcoded score 100) bypass, since real Presidio matches essentially
    /// never score a clean 100.</summary>
    public int BypassReviewScoreThresholdPercent { get; set; } = 100;

    public string Language { get; set; } = "en";
}

public enum ExportType
{
    /// <summary>No type chosen yet — a freshly-added export starts here so the designer doesn't
    /// pre-select a type (and therefore a whole settings panel) the user hasn't picked.</summary>
    None = -1,
    Csv = 0,
    Therefore = 1
    // Xml, etc. added later — new IExportWriter + ExportType value, no data-model change.
}

public enum ExportOutputMode
{
    OneFilePerDocument = 0,
    AppendToSharedFile = 1
}

public enum ExportFileMode
{
    None = 0,
    Original = 1,
    Redacted = 2
}

/// <summary>One configured export destination on a profile — a profile can have several (e.g. a CSV to
/// an accounts folder and another CSV elsewhere). Run by <c>Capture.Export.ProfileExportRunner</c>.</summary>
public sealed class ExportDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New export";
    public bool Enabled { get; set; } = true;
    public ExportType Type { get; set; } = ExportType.None;
    public string OutputFolder { get; set; } = string.Empty;
    public ExportOutputMode OutputMode { get; set; } = ExportOutputMode.OneFilePerDocument;

    /// <summary>Used when <see cref="OutputMode"/> is AppendToSharedFile — the one file every document
    /// appends a row to.</summary>
    public string SharedFileName { get; set; } = "export.csv";

    /// <summary>Used when <see cref="OutputMode"/> is OneFilePerDocument — supports {FieldName} tokens
    /// (any profile field), plus {OriginalFileName}, {DocumentId}, {Date}, and {Time}. Date/time tokens
    /// accept an optional .NET format after a pipe, e.g. {Date|yyyy-MM-dd}.</summary>
    public string FileNamePattern { get; set; } = "{OriginalFileName}";

    /// <summary>Which fields to include, in this order. Empty means "all fields not marked
    /// HideFromIndexing", in the profile's own field order.</summary>
    public List<Guid> FieldIds { get; set; } = [];

    public ExportFileMode FileMode { get; set; } = ExportFileMode.None;
    public bool IncludeHeader { get; set; } = true;

    // Therefore-specific — unused when Type is Csv. FieldIds above is unused for Therefore; mapping
    // (below) replaces it. FileMode still applies (attaches the original/redacted file to the
    // created document's Streams, same fallback as the CSV writer's file copy).
    public int? ThereforeCategoryNo { get; set; }

    /// <summary>Display only — avoids refetching the category just to show the current selection.</summary>
    public string? ThereforeCategoryName { get; set; }
    public List<ThereforeFieldMapping> ThereforeFieldMappings { get; set; } = [];
}

/// <summary>One Therefore category field discovered via the category picker, optionally mapped to one
/// of this profile's own index fields. <see cref="FieldType"/> mirrors
/// <c>Capture.Therefore.ThereforeFieldType</c>'s int values — duplicated as a plain int here rather
/// than referencing that project, since <c>Capture.Core</c> stays dependency-free.</summary>
public sealed class ThereforeFieldMapping
{
    public int FieldNo { get; set; }
    public string Caption { get; set; } = string.Empty;

    /// <summary>The machine identifier used as "FieldName" when writing — distinct from
    /// <see cref="Caption"/> (the human label). See Capture.Therefore.ThereforeCategoryField.</summary>
    public string IndexDataFieldName { get; set; } = string.Empty;
    public int FieldType { get; set; }
    public bool Mandatory { get; set; }
    public Guid? IndexFieldId { get; set; }
}

public sealed class IndexingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public string? SampleFileName { get; set; }
    public string? Locale { get; set; }
    public int AutoReadyThreshold { get; set; } = 80;

    public RedactionSettings Redaction { get; set; } = new();
    public List<ExportDefinition> Exports { get; set; } = [];

    /// <summary>When true, a document that exports successfully (every enabled export definition
    /// succeeded) is deleted from the inbox immediately afterward instead of being marked
    /// <see cref="Capture.Core.Models.DocumentStatus.Exported"/> and kept around.</summary>
    public bool RemoveAfterExport { get; set; }
    public List<IndexField> Fields { get; set; } = [];

    /// <summary>Profile-level C# scripts — see <see cref="FieldScript"/>/<see cref="ScriptTrigger"/>.
    /// Distinct from a <see cref="FieldKind.Script"/> field's own <see cref="IndexField.ScriptExpression"/>:
    /// these are imperative and can write any field, not just their own.</summary>
    public List<FieldScript> Scripts { get; set; } = [];

    /// <summary>C# helper functions (and any other top-level declarations) made available to every
    /// script that runs against this profile — profile-level <see cref="Scripts"/>, every
    /// <see cref="FieldKind.Script"/> field's <see cref="IndexField.ScriptExpression"/>, and every
    /// <see cref="FieldKind.Button"/> field's <see cref="IndexField.ButtonScriptSource"/>. Compiled as a
    /// prefix ahead of each script's own text (see <c>IFieldScriptRunner</c>'s <c>sharedSource</c>
    /// parameter), so a helper declared here doesn't need to be copy-pasted into every script that
    /// wants it.</summary>
    public string SharedScriptSource { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record NormalizedRect(float X, float Y, float Width, float Height);

public sealed record ZoneExtractResult(string Text, float Confidence);
