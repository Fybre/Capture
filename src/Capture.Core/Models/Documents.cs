namespace Capture.Core.Models;

public enum DocumentSource
{
    Import = 0,
    Watch = 1,
    Scan = 2
}

public enum DocumentStatus
{
    Queued = 0,
    Processing = 1,
    NeedsReview = 2,
    Ready = 3,
    Error = 4,
    Exported = 5
}

/// <summary>Where a document stands relative to its profile's redaction settings (see
/// <c>RedactionSettings</c>/<c>RedactionDetectionStep</c>). Independent of <see cref="DocumentStatus"/> —
/// a document can be Ready for indexing purposes while still awaiting redaction review.</summary>
public enum RedactionStatus
{
    /// <summary>Redaction isn't enabled for this document's profile, or it hasn't reached Ready yet.</summary>
    None = 0,
    PendingReview = 1,
    Applied = 2,
    Failed = 3
}

public sealed class CaptureDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string OriginalFileName { get; init; }
    public required string StoredPath { get; set; }
    public DocumentSource Source { get; init; }
    public Guid? ProfileId { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? ImportProfileId { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Queued;
    public int PageCount { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? ErrorMessage { get; set; }
    public RedactionStatus RedactionStatus { get; set; } = RedactionStatus.None;
    public string? RedactedPath { get; set; }
    public string? RedactionError { get; set; }

    /// <summary>Null means active. Set by <c>IDocumentStore.SoftDeleteAsync</c> — the document still
    /// exists (row + on-disk files intact) but is excluded from <c>GetAllAsync</c> and shown in the
    /// Trash view instead, until <c>RestoreAsync</c> or a real <c>PurgeAsync</c>.</summary>
    public DateTimeOffset? DeletedUtc { get; set; }

    /// <summary>SHA-256 (hex) of the original source file's bytes, computed once at import time — see
    /// <c>MainViewModel.ImportPathsAsync</c>. Null for documents imported before this existed, and for
    /// scanned documents (no single source file to hash). Used by <c>IDocumentStore.FindByContentHashAsync</c>
    /// to detect a re-imported file; "duplicate" itself is derived by comparing this against other
    /// active documents' hashes on the fly, not stored as its own flag, so it never goes stale if the
    /// document(s) it matches are later removed, restored, or re-imported.</summary>
    public string? ContentHash { get; set; }
}

public sealed class DocumentPage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid DocumentId { get; init; }
    public required int PageNumber { get; init; }

    /// <summary>This page's number within the document's own <c>StoredPath</c> file. Equals
    /// <see cref="PageNumber"/> when the document was imported as a single file. Differs when this
    /// document is one of several produced by splitting one source file — <c>StoredPath</c> still holds
    /// the *original* multi-page file (not a trimmed copy), so text/OCR extraction needs this to find the
    /// right page, while <see cref="PageNumber"/> stays the renumbered 1..N used everywhere else.</summary>
    public required int SourcePageNumber { get; init; }

    public required string ImagePath { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dpi { get; set; }
}

public sealed class CaptureBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public int Number { get; init; }
    public Guid? WatchFolderEntryId { get; init; }
}

public sealed record RasterPage(
    int PageNumber,
    string ImagePath,
    int Width,
    int Height,
    int Dpi);
