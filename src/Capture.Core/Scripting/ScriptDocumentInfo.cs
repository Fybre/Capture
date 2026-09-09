using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;

namespace Capture.Core.Scripting;

/// <summary>Document-level facts exposed to a script as <c>Document</c> — separate from
/// <see cref="ScriptExecutionContext.Values"/> (the per-field index values) since these describe the
/// document as a whole, not any one field.</summary>
public sealed class ScriptDocumentInfo
{
    public required string FileName { get; init; }

    /// <summary>Lowercase, including the leading dot (e.g. <c>".pdf"</c>), or empty when unknown (a
    /// blank/new profile with no sample, or a document with no recognizable extension).</summary>
    public required string FileExtension { get; init; }

    public required int PageCount { get; init; }

    /// <summary>The same OCR/PDF text extraction the AI field pipeline sends to a language model —
    /// every page's recognized text, joined with a <c>--- Page N ---</c> header per page. See
    /// <c>DocumentText.FromLattices</c>. Empty when built with no lattices available (e.g. at export
    /// time — see <c>ProfileExportRunner</c>).</summary>
    public required string Text { get; init; }

    /// <summary>The single canonical builder — used by <c>ProfileApplicator</c> (extraction time, full
    /// lattices), <c>ProfileExportRunner</c> (export time, no lattices so <see cref="Text"/> comes back
    /// empty), and <c>MainViewModel</c>'s review-panel button handler alike, so all three ways a script
    /// can run see this built the same way.</summary>
    public static ScriptDocumentInfo From(IReadOnlyList<PageLattice> lattices, CaptureDocument? document) => new()
    {
        FileName = document?.OriginalFileName ?? string.Empty,
        FileExtension = string.IsNullOrEmpty(document?.OriginalFileName)
            ? string.Empty
            : Path.GetExtension(document.OriginalFileName).ToLowerInvariant(),
        PageCount = document?.PageCount ?? lattices.Count,
        Text = DocumentText.FromLattices(lattices)
    };
}
