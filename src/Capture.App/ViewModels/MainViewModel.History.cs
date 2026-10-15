using System.Diagnostics;
using Capture.Core.CaptureProfiles;
using Capture.Core.Models;
using Capture.Core.Store;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

/// <summary>Each document's history — see <see cref="DocumentEvent"/>. Steps a person takes are
/// recorded here; what Capture does on import is recorded by CapturePlanMaterializer.</summary>
public partial class MainViewModel
{
    /// <summary>How long a field goes unchanged before an edit to it is recorded — so typing a value is
    /// one step in the history, not one per keystroke.</summary>
    private static readonly TimeSpan FieldEditSettleTime = TimeSpan.FromSeconds(1.5);

    private readonly Dictionary<(Guid Document, Guid Field), CancellationTokenSource> _pendingFieldEdits = [];

    /// <summary>History starts collapsed; once opened it stays open while moving between documents.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryChevron))]
    private bool _isHistoryExpanded;

    public string HistoryChevron => IsHistoryExpanded ? "▾" : "▸";

    [RelayCommand]
    private void ToggleHistory() => IsHistoryExpanded = !IsHistoryExpanded;

    private Task RecordAsync(DocumentRow row, DocumentEventKind kind, string message, string? detail = null, TimeSpan? duration = null) =>
        RecordAsync([row], kind, message, detail, duration);

    /// <summary>Records one step, done by the signed-in user, on each of these documents.</summary>
    private async Task RecordAsync(IEnumerable<DocumentRow> rows, DocumentEventKind kind, string message, string? detail = null, TimeSpan? duration = null)
    {
        var recorded = rows.Distinct().Select(row => (row, item: DocumentEvent.ByUser(row.Id, kind, message, detail, duration))).ToList();
        await _events.RecordAsync(recorded.Select(entry => entry.item).ToList()).ConfigureAwait(true);
        foreach (var (row, item) in recorded)
            row.AddHistory([item]);
    }

    /// <summary>Records something Capture did on its own while a person was working — e.g. redaction
    /// found after Mark ready.</summary>
    private async Task RecordByCaptureAsync(DocumentRow row, DocumentEventKind kind, string message, string? detail = null, TimeSpan? duration = null)
    {
        var item = DocumentEvent.ByCapture(row.Id, kind, message, detail, duration);
        await _events.RecordAsync(item).ConfigureAwait(true);
        row.AddHistory([item]);
    }

    private async Task LoadHistoryAsync(DocumentRow? row)
    {
        if (row is null || _events is null)
            return;
        try
        {
            row.SetHistory(await _events.GetEventsAsync(row.Id).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Could not load the history of document {row.Id}: {ex}");
        }
    }

    /// <summary>Gives a document split off another the history it came with, so its trail still starts
    /// at the import.</summary>
    private async Task CopyHistoryAsync(Guid fromDocumentId, DocumentRow to)
    {
        if (_events is null)
            return;
        try
        {
            var inherited = (await _events.GetEventsAsync(fromDocumentId).ConfigureAwait(true))
                .Select(item => item with { DocumentId = to.Id })
                .ToList();
            await _events.RecordAsync(inherited).ConfigureAwait(true);
            to.AddHistory(inherited);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Could not copy the history of document {fromDocumentId}: {ex}");
        }
    }

    /// <summary>Records a field changed by hand once it's been left alone for a moment. A batch field is
    /// shared, so its change goes on every document in the batch. A sensitive field's values aren't
    /// written down.</summary>
    private void TrackFieldEdit(DocumentRow document, IndexValue value, bool isBatch, Func<string> recorded, Action<string> setRecorded)
    {
        var key = (document.Id, value.FieldId);
        if (_pendingFieldEdits.Remove(key, out var previous))
            previous.Cancel();
        var delay = new CancellationTokenSource();
        _pendingFieldEdits[key] = delay;
        _ = RecordWhenSettledAsync();

        async Task RecordWhenSettledAsync()
        {
            try
            {
                await Task.Delay(FieldEditSettleTime, delay.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (_pendingFieldEdits.TryGetValue(key, out var current) && current == delay)
                _pendingFieldEdits.Remove(key);

            var before = recorded();
            var after = value.Value;
            if (string.Equals(before, after, StringComparison.Ordinal))
                return;
            setRecorded(after);
            var rows = isBatch && document.Document.BatchId is { } batchId
                ? Documents.Where(row => row.Document.BatchId == batchId).Append(document)
                : [document];
            await RecordAsync(rows, DocumentEventKind.Edit,
                $"Changed {(isBatch ? "batch field " : string.Empty)}{value.FieldName}",
                ChangeDetail(value, before, after)).ConfigureAwait(true);
        }
    }

    private static string ChangeDetail(IndexValue value, string before, string after) =>
        value.Sensitive
            ? "Sensitive field — its values aren't recorded."
            : $"Was: {Shown(before)}\nNow: {Shown(after)}";

    private static string Shown(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var trimmed = value.Trim();
        return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
    }

    /// <summary>Records what redaction did when a step a person took set it off (Mark ready).</summary>
    private Task RecordRedactionChangeAsync(DocumentRow row, RedactionStatus before, TimeSpan? duration = null) =>
        row.Document.RedactionStatus == before ? Task.CompletedTask : row.Document.RedactionStatus switch
        {
            RedactionStatus.PendingReview => RecordByCaptureAsync(row, DocumentEventKind.Redaction, "Found information to redact — waiting for review", duration: duration),
            RedactionStatus.Applied => RecordByCaptureAsync(row, DocumentEventKind.Redaction, "Redacted automatically", duration: duration),
            RedactionStatus.Failed => RecordByCaptureAsync(row, DocumentEventKind.Error, "Redaction failed", row.Document.RedactionError, duration),
            _ => Task.CompletedTask
        };

    private static string Pages(IReadOnlyList<int> pageNumbers) => CapturePlanMaterializer.PageList(pageNumbers);
}
