namespace Capture.Core.Profiles;

public enum FieldKind
{
    Zonal = 0,
    KeyValue = 1,
    Regex = 2,
    Macro = 3,
    Barcode = 4,
    Ai = 5,

    /// <summary>Mirrors whatever value the batch profile's trigger (barcode/regex) captured for this
    /// document, if any — supplied ambiently at import time, no zone/pattern configured on the field itself.</summary>
    BatchSeparatorValue = 6
}

public enum MacroSegmentKind
{
    Literal = 0,
    DocumentCounter = 1,
    BatchCounter = 2,
    DateTime = 3,
    Field = 4,
    ProfileName = 5
}

public sealed class MacroSegment
{
    public MacroSegmentKind Kind { get; set; }
    public string? Text { get; set; }
    public int CounterWidth { get; set; }
}

public enum IndexLevel
{
    Document = 0,
    Batch = 1
}

public enum DocumentSeparationTrigger
{
    None = 0,
    Barcode = 1,
    BlankPage = 2,
    EveryNPages = 3
}

/// <summary>How a profile splits one imported file into multiple documents. Independent of any
/// per-field configuration — a Barcode trigger just references an already-defined Barcode-kind field.</summary>
public sealed class DocumentSeparation
{
    public DocumentSeparationTrigger Trigger { get; set; } = DocumentSeparationTrigger.None;

    /// <summary>Used when <see cref="Trigger"/> is <see cref="DocumentSeparationTrigger.Barcode"/> — the Id
    /// of a <c>Kind == Barcode</c> field on this same profile. That field's own Zone/ValuePattern is reused
    /// for matching; nothing barcode-specific is configured here.</summary>
    public Guid? BarcodeFieldId { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="DocumentSeparationTrigger.EveryNPages"/>.</summary>
    public int PageCount { get; set; } = 1;

    /// <summary>Used when <see cref="Trigger"/> is <see cref="DocumentSeparationTrigger.Barcode"/> or
    /// <see cref="DocumentSeparationTrigger.BlankPage"/> — there's no separator page to discard for EveryNPages.</summary>
    public bool DiscardSeparatorPage { get; set; }
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
    public List<MacroSegment> Macro { get; set; } = [];

    // Legacy per-field document-separation flags. Kept only so JsonProfileStore can migrate profiles
    // saved before separation moved to IndexingProfile.Separation — new code should read/write that
    // instead. See JsonProfileStore.MigrateLegacySeparation.
    public bool SeparatesDocuments { get; set; }
    public bool DiscardPage { get; set; }

    public bool HideFromIndexing { get; set; }
    public string? BarcodeFormat { get; set; }
    public string? AiTypeId { get; set; }
    public string? AiPrompt { get; set; }
}

public sealed class IndexingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public string? SampleFileName { get; set; }
    public string? Locale { get; set; }
    public int AutoReadyThreshold { get; set; } = 80;

    // Legacy — kept only so JsonProfileStore can migrate profiles saved before separation moved to
    // Separation below. New code should read/write Separation instead.
    public bool SplitOnBlankPages { get; set; }

    public float BlankInkPercent { get; set; } = 1;
    public DocumentSeparation Separation { get; set; } = new();
    public List<IndexField> Fields { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record NormalizedRect(float X, float Y, float Width, float Height);

public sealed record ZoneExtractResult(string Text, float Confidence);
