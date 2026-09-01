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

    public int ConfidenceThreshold { get; set; } = 80;

    public string? Locale { get; set; }

    public string? ProfileName { get; set; }

    public IReadOnlyList<IndexValue> BatchIndexes { get; private set; } = [];

    public IReadOnlyList<IndexValue> DocumentIndexes { get; private set; } = [];

    public IReadOnlyList<IndexValue> Indexes => BatchIndexes.Concat(DocumentIndexes).ToList();

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

    public string IndexesSummary
    {
        get
        {
            var visible = Indexes.Where(index => !index.HideFromIndexing).ToList();
            if (visible.Count == 0)
                return string.Empty;
            return string.Join("  ·  ", visible.Select(index =>
                string.IsNullOrWhiteSpace(index.Value)
                    ? index.FieldName
                    : $"{index.FieldName}={index.Value}"));
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
        OnPropertyChanged(nameof(Indexes));
        // PageCount reads straight through to Document.PageCount, which isn't itself observable — a
        // page delete/split/reorder mutates it in place on this same row (see MainViewModel's page
        // management commands) rather than replacing the row, so the Page column needs an explicit
        // nudge here alongside everything else this method already refreshes after such a mutation.
        OnPropertyChanged(nameof(PageCount));
    }
}
