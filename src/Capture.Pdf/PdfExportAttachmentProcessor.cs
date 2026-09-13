using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Store;
using Capture.Export;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Capture.Pdf;

/// <summary>Implements <see cref="ExportDefinition.Pdfa"/>/<see cref="ExportDefinition.SearchablePdf"/>
/// on top of <see cref="ExportSourceFile.Resolve"/>. When neither flag is set, this is a pass-through —
/// no new file is written and every export keeps behaving exactly as before. Otherwise rebuilds a fresh
/// PDF from the document's page images (same pixel-to-point conversion as
/// <see cref="PdfPigMergedDocumentWriter"/>), best-effort PDF/A-shaping it via PdfPig's own
/// <see cref="PdfAStandard"/> support and/or overlaying an invisible OCR text layer via
/// <see cref="SearchablePdfSupport"/>.</summary>
public sealed class PdfExportAttachmentProcessor : IExportAttachmentProcessor
{
    private readonly IDocumentStore _documents;
    private readonly ILatticeStore _lattice;
    private readonly IRedactionCandidateStore _redactionCandidates;
    private readonly IAppPaths _paths;

    public PdfExportAttachmentProcessor(
        IDocumentStore documents, ILatticeStore lattice, IRedactionCandidateStore redactionCandidates, IAppPaths paths)
    {
        _documents = documents;
        _lattice = lattice;
        _redactionCandidates = redactionCandidates;
        _paths = paths;
    }

    public async Task<string> ResolveAsync(
        ExportDefinition definition, CaptureDocument document, CancellationToken cancellationToken = default)
    {
        var sourcePath = ExportSourceFile.Resolve(definition, document);
        if (!definition.Pdfa && !definition.SearchablePdf)
            return sourcePath;

        var isRedactedCopy = !string.IsNullOrEmpty(document.RedactedPath)
            && string.Equals(sourcePath, document.RedactedPath, StringComparison.OrdinalIgnoreCase);
        var confirmedRedactionsByPage = isRedactedCopy
            ? (await _redactionCandidates.GetAsync(document.Id, cancellationToken).ConfigureAwait(false))
                .Where(candidate => candidate.Decision != RedactionDecision.Rejected)
                .GroupBy(candidate => candidate.PageNumber)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<RedactionCandidate>)group.ToList())
            : [];

        var pages = await _documents.GetPagesAsync(document.Id, cancellationToken).ConfigureAwait(false);

        using var builder = new PdfDocumentBuilder();
        if (definition.Pdfa)
            builder.ArchiveStandard = PdfAStandard.A2B;

        var font = builder.AddTrueTypeFont(SearchablePdfSupport.EmbeddedFont.Value);

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = SKBitmap.Decode(page.ImagePath)
                ?? throw new InvalidOperationException($"Unable to decode page image '{page.ImagePath}'.");
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);

            // Same conversion as PdfPigMergedDocumentWriter/PdfPigExportWriter — AddPage's MediaBox is
            // in PDF points, not pixels.
            var dpi = page.Dpi > 0 ? page.Dpi : 96;
            var widthPoints = bitmap.Width * 72.0 / dpi;
            var heightPoints = bitmap.Height * 72.0 / dpi;

            var pdfPage = builder.AddPage(widthPoints, heightPoints);
            pdfPage.AddPng(encoded.ToArray(), new PdfRectangle(0, 0, widthPoints, heightPoints));

            if (definition.SearchablePdf)
            {
                var lattice = await _lattice.GetAsync(document.Id, page.PageNumber, cancellationToken).ConfigureAwait(false);
                if (lattice is { Words.Count: > 0 })
                {
                    var confirmedOnPage = confirmedRedactionsByPage.GetValueOrDefault(page.PageNumber, []);
                    SearchablePdfSupport.EmbedInvisibleWords(pdfPage, font, lattice, widthPoints, heightPoints, confirmedOnPage);
                }
            }
        }

        var outputPath = Path.Combine(TempDirectory(document.Id), $"{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, builder.Build(), cancellationToken).ConfigureAwait(false);
        return outputPath;
    }

    public Task CleanupAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var directory = TempDirectory(documentId);
        if (Directory.Exists(directory))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* Best effort — a leftover temp file here never affects correctness. */ }
        }
        return Task.CompletedTask;
    }

    private string TempDirectory(Guid documentId) =>
        Path.Combine(_paths.WorkDirectory, "export-temp", documentId.ToString("N"));
}
