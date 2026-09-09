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
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
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
