using Capture.Core.Import;
using Capture.Core.Profiles;

namespace Capture.Core.CaptureProfiles;

public sealed class CaptureProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New capture profile";
    public bool Enabled { get; set; } = true;
    public BatchDefinition Batch { get; set; } = new();
    public List<DocumentTypeDefinition> DocumentTypes { get; set; } = [];
    public Guid? DefaultDocumentTypeId { get; set; }
    public bool FileIsDocumentBoundary { get; set; } = true;
    public bool AutoExportReadyDocuments { get; set; }

    /// <summary>Discards every blank page on ingestion, before any other work — OCR, barcode/zone/
    /// blank-page rule evaluation, batch/document classification. Because a discarded page is invisible
    /// to every rule, a BlankPage separation rule on the batch or on any document type can never match
    /// while this is enabled; its trigger page is gone before rule evaluation starts.</summary>
    public bool RemoveBlankPagesOnIngestion { get; set; }

    /// <summary>Ink-coverage threshold for <see cref="RemoveBlankPagesOnIngestion"/> — same meaning and
    /// scale as a BlankPage separation rule's own <see cref="SeparationStrategy.BlankInkPercent"/>.</summary>
    public float RemoveBlankPagesInkPercent { get; set; } = 1f;

    /// <summary>Lets AI decide where each document starts and which document type it is, using each
    /// type's <see cref="DocumentTypeDefinition.AiDescription"/>. Rules still win where they match:
    /// batch rules, separator sheets, and a type's start or recognition rules.</summary>
    public bool AiSeparation { get; set; }

    /// <summary>With <see cref="AiSeparation"/> on: true lets the AI choose each document's type too;
    /// false has it only split documents, each then typed by its pages' rules or given the fallback
    /// type.</summary>
    public bool AiClassifies { get; set; } = true;

    /// <summary>Below this confidence (0–100), an AI split or type is still applied, but the document is
    /// flagged for review with the AI's reason.</summary>
    public int AiSeparationMinConfidence { get; set; } = 70;

    /// <summary>Extra rules for the AI about this profile's documents, added to its separation rules —
    /// e.g. "a TRANSMITTAL cover sheet always starts a new document".</summary>
    public string? AiSeparationGuidance { get; set; }

    /// <summary>When to send scanned pages to an external OCR service instead of, or after, the built-in
    /// engine. Pages with a real PDF text layer never need OCR.</summary>
    public ExternalOcrMode ExternalOcr { get; set; }

    /// <summary>The Azure Document Intelligence connection used for external OCR; null uses the first one.</summary>
    public Guid? ExternalOcrConnectionId { get; set; }

    /// <summary>For <see cref="ExternalOcrMode.WhenPoor"/>: a page whose built-in OCR averages below this
    /// word confidence (0–100), or doesn't read as real text, is sent to the external service.</summary>
    public int ExternalOcrMinConfidence { get; set; } = 70;

    public OcrOptions OcrOptions => new(ExternalOcr, ExternalOcrConnectionId, ExternalOcrMinConfidence);

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum ExternalOcrMode
{
    /// <summary>Built-in OCR only.</summary>
    Off = 0,

    /// <summary>Every scanned page goes to the external service.</summary>
    AllPages = 1,

    /// <summary>Built-in OCR first; pages it reads poorly (e.g. handwriting) go to the external service.</summary>
    WhenPoor = 2
}

/// <summary>How a page is OCR'd: a capture profile's external OCR settings.</summary>
public sealed record OcrOptions(ExternalOcrMode Mode, Guid? ConnectionId = null, int MinConfidence = 70)
{
    public static readonly OcrOptions BuiltInOnly = new(ExternalOcrMode.Off);
}

public sealed class BatchDefinition
{
    public string? SampleFileName { get; set; }
    public RuleSet StartRules { get; set; } = new() { MatchMode = SeparationMatchMode.None };
    public List<IndexField> Fields { get; set; } = [];
    public List<FieldScript> Scripts { get; set; } = [];
    public string SharedScriptSource { get; set; } = string.Empty;
    public PageDisposition TriggerPageDisposition { get; set; }
    public bool StartNewBatchForEachFile { get; set; }
}

public sealed class DocumentTypeDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New document type";
    public string? SampleFileName { get; set; }
    public RuleSet RecognitionRules { get; set; } = new();
    /// <summary>When true, a successful recognition match also acts as a document boundary. This is
    /// useful when the identifying marker appears only on the first page; leave it false when the
    /// same identifying content can appear on continuation pages.</summary>
    public bool IdentificationStartsNewDocument { get; set; }
    public RuleSet StartRules { get; set; } = new();
    public List<IndexField> Fields { get; set; } = [];
    public List<FieldScript> Scripts { get; set; } = [];
    public string SharedScriptSource { get; set; } = string.Empty;
    public PageDisposition TriggerPageDisposition { get; set; }
    public RedactionSettings Redaction { get; set; } = new();
    public List<ExportDefinition> Exports { get; set; } = [];
    public string? Locale { get; set; }
    public int AutoReadyThreshold { get; set; } = 80;

    /// <summary>The AI document type catalog entry this type was added from, if any.</summary>
    public string? AiCatalogTypeId { get; set; }

    /// <summary>What a document of this type looks like, for AI separation — e.g. "a supplier's bill
    /// requesting payment". Filled in from the catalog and editable.</summary>
    public string? AiDescription { get; set; }

    /// <summary>When false, a page the AI sees as the start of a new document of this type joins the
    /// previous document instead — for attachments that travel with another document.</summary>
    public bool AiStartsNewDocument { get; set; } = true;

    /// <summary>When true and AI separation is on, this type's recognition and start rules don't split
    /// or type documents — the AI does. Rule matches still fill boundary-rule fields on a document the
    /// AI typed as this one, and the rules apply as before on pages the AI couldn't analyse.</summary>
    public bool AiIgnoresRules { get; set; }
}

public sealed class RuleSet
{
    public List<SeparationStrategy> Rules { get; set; } = [];
    public SeparationMatchMode MatchMode { get; set; } = SeparationMatchMode.Any;
    public int MatchMinimum { get; set; } = 1;
}

public enum PageDisposition
{
    IncludeInNewDocument = 0,
    Consume = 1
}

public interface ICaptureProfileStore
{
    Task<IReadOnlyList<CaptureProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<CaptureProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
