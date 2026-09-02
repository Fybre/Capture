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
    /// <c>DocumentText.FromLattices</c>.</summary>
    public required string Text { get; init; }
}
