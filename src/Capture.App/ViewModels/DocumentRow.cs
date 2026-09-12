using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Capture.App.ViewModels;

public sealed partial class DocumentRow : ObservableObject
{
    public DocumentRow(CaptureDocument document)
    {
        Document = document;
    }

    public CaptureDocument Document { get; }

    public Guid Id => Document.Id;

    public string FileName => Document.OriginalFileName;

    public int PageCount => Document.PageCount;

    [ObservableProperty]
    private bool _batchAccent;

    /// <summary>True for the first row of a new batch in whichever ordered sequence last computed it —
    /// the master flat list for the Inbox rail, or one profile group's filtered list for Table mode (see
    /// MainViewModel.RefreshBatchAccents/RefreshDocumentGroups). Drives the batch-divider row shown
    /// immediately above this one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchDividerLabel))]
    private bool _isFirstInBatch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchDividerLabel))]
    private int? _batchNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchDividerLabel))]
    private string? _batchInputChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchDividerLabel))]
    private int _batchDocumentCount;

    /// <summary>"Batch 41 · Scanner · 3 docs" — blank when batch metadata hasn't loaded yet (e.g. a
    /// batch created moments ago, before the next full reload populates the lookup), in which case the
    /// divider still shows via <see cref="IsFirstInBatch"/>, just without this text.</summary>
    public string BatchDividerLabel
    {
        get
        {
            if (BatchNumber is not { } number)
                return string.Empty;
            var parts = new List<string> { $"Batch {number}" };
            if (!string.IsNullOrWhiteSpace(BatchInputChannel))
                parts.Add(BatchInputChannel);
            parts.Add(BatchDocumentCount == 1 ? "1 doc" : $"{BatchDocumentCount} docs");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Set by MainViewModel.RefreshDuplicateFlags — true when another active source import has
    /// the same ContentHash. Sibling documents split from one source are excluded. Derived on the fly
    /// rather than persisted, so it can't go stale when a match is removed/restored/re-imported.</summary>
    [ObservableProperty]
    private bool _isDuplicate;

    /// <summary>Tooltip text for the duplicate indicator — empty when <see cref="IsDuplicate"/> is false.</summary>
    [ObservableProperty]
    private string _duplicateTooltip = string.Empty;

    public int ConfidenceThreshold { get; set; } = 80;

    public string? Locale { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentTypeDisplay))]
    [NotifyPropertyChangedFor(nameof(DocumentTypeMarker))]
    [NotifyPropertyChangedFor(nameof(DocumentTypeTooltip))]
    private string? _profileName;

    /// <summary>The document type assigned by capture recognition (or later assignment). ProfileName
    /// retains its existing name because it is also used by the table grouping code.</summary>
    public string DocumentTypeDisplay => string.IsNullOrWhiteSpace(ProfileName) ? "Unclassified" : ProfileName;

    public string DocumentTypeMarker => string.IsNullOrWhiteSpace(ProfileName) ? "?" : "T";

    public string DocumentTypeTooltip => $"Document type: {DocumentTypeDisplay}";

    public IReadOnlyList<IndexValue> BatchIndexes { get; private set; } = [];

    public IReadOnlyList<IndexValue> DocumentIndexes { get; private set; } = [];

    public IReadOnlyList<IndexValue> Indexes => BatchIndexes.Concat(DocumentIndexes).ToList();

    // Dynamic Table columns convert the complete row because field names and scopes are only known
    // when each column is created. Give those bindings an explicit property to observe; binding to
    // the row object itself does not reliably re-run converters when an IndexValue is edited in place.
    public DocumentRow IndexCellSource => this;

    public string StatusDisplay => Document.Status switch
    {
        DocumentStatus.Queued => "Queued",
        DocumentStatus.Processing => "Processing",
        DocumentStatus.NeedsReview => "Needs review",
        DocumentStatus.Ready => "Ready",
        DocumentStatus.Error => "Error",
        DocumentStatus.Exported => "Exported",
        _ => Document.Status.ToString()
    };

    public string RedactionStatusDisplay => Document.RedactionStatus switch
    {
        RedactionStatus.PendingReview => "Redaction pending",
        RedactionStatus.Applied => "Redacted",
        RedactionStatus.Failed => "Redaction failed",
        _ => string.Empty
    };

    public bool IsRedactionPending => Document.RedactionStatus == RedactionStatus.PendingReview;

    public bool IsRedactionApplied => Document.RedactionStatus == RedactionStatus.Applied;

    public bool IsRedactionFailed => Document.RedactionStatus == RedactionStatus.Failed;

    public string RedactionStatusTooltip => IsRedactionFailed && !string.IsNullOrWhiteSpace(Document.RedactionError)
        ? $"Redaction failed: {Document.RedactionError}"
        : RedactionStatusDisplay;

    public string IndexesSummary
    {
        get
        {
            var visible = Indexes.Where(index => !index.HideFromIndexing).ToList();
            if (visible.Count == 0)
                return string.Empty;
            var sensitiveBatchFieldIds = BatchIndexes
                .Where(index => index.Sensitive)
                .Select(index => index.FieldId)
                .ToHashSet();
            return string.Join("  ·  ", visible.Select(index =>
                string.IsNullOrWhiteSpace(index.Value)
                    ? index.FieldName
                    : $"{index.FieldName}={(sensitiveBatchFieldIds.Contains(index.FieldId) ? "••••••" : index.Value)}"));
        }
    }

    public string IssueDisplay
    {
        get
        {
            var visible = Indexes.Where(index => !index.HideFromIndexing).ToList();
            var missing = visible.Count(index => index.IsMissing);
            var invalid = visible.Count(index => index.ValidationError is not null);
            var low = visible.Count(index => index.IsLowConfidence(ConfidenceThreshold));
            if (missing == 0 && invalid == 0 && low == 0)
                return string.Empty;
            var parts = new List<string>();
            if (missing > 0)
                parts.Add($"{missing} missing");
            if (invalid > 0)
                parts.Add($"{invalid} invalid");
            if (low > 0)
                parts.Add($"{low} low conf");
            return string.Join(", ", parts);
        }
    }

    public void SetDocumentIndexes(IReadOnlyList<IndexValue> values)
    {
        DocumentIndexes = values;
        RecalcStatus();
    }

    public void SetBatchIndexes(IReadOnlyList<IndexValue> values)
    {
        BatchIndexes = values;
        RecalcStatus();
    }

    public void RecalcStatus()
    {
        if (Document.Status != DocumentStatus.Error && Indexes.Count > 0)
            Document.Status = IndexFormat.StatusFor(Indexes, ConfidenceThreshold);
        NotifyIndexes();
    }

    public void NotifyIndexes()
    {
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(IndexesSummary));
        OnPropertyChanged(nameof(IssueDisplay));
        OnPropertyChanged(nameof(RedactionStatusDisplay));
        OnPropertyChanged(nameof(IsRedactionPending));
        OnPropertyChanged(nameof(IsRedactionApplied));
        OnPropertyChanged(nameof(IsRedactionFailed));
        OnPropertyChanged(nameof(RedactionStatusTooltip));
        OnPropertyChanged(nameof(Indexes));
        OnPropertyChanged(nameof(IndexCellSource));
        // PageCount reads straight through to Document.PageCount, which isn't itself observable — a
        // page delete/split/reorder mutates it in place on this same row (see MainViewModel's page
        // management commands) rather than replacing the row, so the Page column needs an explicit
        // nudge here alongside everything else this method already refreshes after such a mutation.
        OnPropertyChanged(nameof(PageCount));
    }
}
