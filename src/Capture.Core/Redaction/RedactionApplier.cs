using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Store;

namespace Capture.Core.Redaction;

/// <summary>Single shared orchestration point for actually burning redaction candidates into a
/// document's redacted export copy. Used both by <c>RedactionDetectionStep</c>'s auto-bypass path and
/// by the user-triggered "Apply redactions" action, so there's exactly one place that writes the
/// output PDF and updates the document's redaction fields, regardless of how a document got here.</summary>
public sealed class RedactionApplier
{
    private readonly IRedactedDocumentWriter _writer;
    private readonly IDocumentStore _store;
    private readonly IAppPaths _paths;

    public RedactionApplier(IRedactedDocumentWriter writer, IDocumentStore store, IAppPaths paths)
    {
        _writer = writer;
        _store = store;
        _paths = paths;
    }

    public async Task ApplyAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        IReadOnlyList<RedactionCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var confirmed = candidates.Where(item => item.Decision != RedactionDecision.Rejected).ToList();
        try
        {
            document.RedactedPath = await _writer
                .WriteAsync(document, pages, confirmed, _paths.DocumentRedactedPath(document.Id), cancellationToken)
                .ConfigureAwait(false);
            document.RedactionStatus = RedactionStatus.Applied;
            document.RedactionError = null;
        }
        catch (Exception ex)
        {
            document.RedactionStatus = RedactionStatus.Failed;
            document.RedactionError = ex.Message;
        }

        await _store.UpdateAsync(document, cancellationToken).ConfigureAwait(false);
    }
}
