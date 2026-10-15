using System.Diagnostics;
using Capture.Core.Models;

namespace Capture.Core.Store;

/// <summary>Each document's history — see <see cref="DocumentEvent"/>. Removed with the document when
/// it's purged.</summary>
public interface IDocumentEventStore
{
    Task AddEventsAsync(IReadOnlyList<DocumentEvent> events, CancellationToken cancellationToken = default);

    /// <summary>The document's events, oldest first.</summary>
    Task<IReadOnlyList<DocumentEvent>> GetEventsAsync(Guid documentId, CancellationToken cancellationToken = default);
}

public static class DocumentEventStoreExtensions
{
    /// <summary>Records events without ever failing the step they describe: a history that can't be
    /// written is logged, and the import, edit or export carries on.</summary>
    public static async Task RecordAsync(this IDocumentEventStore? store, IReadOnlyList<DocumentEvent> events, CancellationToken cancellationToken = default)
    {
        if (store is null || events.Count == 0)
            return;
        try
        {
            await store.AddEventsAsync(events, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError($"Could not record document history: {ex}");
        }
    }

    public static Task RecordAsync(this IDocumentEventStore? store, DocumentEvent documentEvent, CancellationToken cancellationToken = default) =>
        store.RecordAsync([documentEvent], cancellationToken);
}
