using Capture.Core.Models;

namespace Capture.Core.Redaction;

/// <summary>Produces a truly-redacted export copy of a document: pages are rasterized with the
/// confirmed redaction boxes burned in as solid fills and rebuilt into a new, image-only PDF — no
/// text/vector content survives underneath. The source document/pages are never modified.</summary>
public interface IRedactedDocumentWriter
{
    Task<string> WriteAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        IReadOnlyList<RedactionCandidate> confirmedCandidates,
        string outputPath,
        CancellationToken cancellationToken = default);
}
