using System.Diagnostics;
using Capture.Core.Import;
using Capture.Core.Models;
using Capture.Core.Paths;

namespace Capture.Core.Lattice;

public sealed class LatticeBuilder : ILatticeBuilder
{
    public const int OcrDpi = 300;

    private readonly IAppPaths _paths;
    private readonly ILatticeStore _store;
    private readonly IPdfTextExtractor _pdfText;
    private readonly IOcrEngine _ocr;
    private readonly IPdfRasterizer _pdfRasterizer;

    public LatticeBuilder(
        IAppPaths paths,
        ILatticeStore store,
        IPdfTextExtractor pdfText,
        IOcrEngine ocr,
        IPdfRasterizer pdfRasterizer)
    {
        _paths = paths;
        _store = store;
        _pdfText = pdfText;
        _ocr = ocr;
        _pdfRasterizer = pdfRasterizer;
    }

    public async Task BuildDocumentAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        CancellationToken cancellationToken = default)
    {
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lattice = await BuildPageAsync(document, page, cancellationToken).ConfigureAwait(false);
                await _store.SaveAsync(document.Id, lattice, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller's own token fired — the page was never actually processed, so it must not
                // be recorded as a successfully-processed empty page (that would look identical to a
                // genuine "no text found" result). Propagate so the whole import is understood to have
                // been interrupted, not silently completed.
                throw;
            }
            catch (Exception ex)
            {
                // A real extraction failure (missing Tesseract data, a corrupt image, a PDF rendering
                // error, ...) must not look identical to a genuine "this page has no text" result — that
                // silent conflation is exactly what let the tessdata/configs/tsv bug ship unnoticed
                // earlier. ex.Message often carries real diagnostic detail already (e.g.
                // TesseractCliOcrEngine surfaces Tesseract's own stderr) that would otherwise be thrown
                // away here.
                Trace.TraceError($"OCR/lattice build failed for document {document.Id} page {page.PageNumber}: {ex.Message}");
                await _store.SaveAsync(document.Id, Empty(page), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<PageLattice> BuildPageAsync(
        CaptureDocument document,
        DocumentPage page,
        CancellationToken cancellationToken = default)
    {
        var isPdf = ImportFormats.IsPdf(document.StoredPath);

        if (isPdf)
        {
            var pdfWords = await _pdfText.TryExtractPageAsync(document.StoredPath, page.SourcePageNumber, cancellationToken)
                .ConfigureAwait(false);
            if (pdfWords is { Count: > 0 } && LatticeQuality.LooksLikeRealText(pdfWords))
            {
                return new PageLattice
                {
                    PageNumber = page.PageNumber,
                    PixelWidth = page.Width,
                    PixelHeight = page.Height,
                    Dpi = page.Dpi,
                    Source = LatticeSource.PdfText,
                    Words = pdfWords
                };
            }
        }

        var ocrImagePath = page.ImagePath;
        var ocrDpi = page.Dpi <= 0 ? 96 : page.Dpi;

        if (isPdf)
        {
            var ocrDir = _paths.DocumentOcrDirectory(document.Id);
            Directory.CreateDirectory(ocrDir);
            ocrImagePath = Path.Combine(ocrDir, $"{page.PageNumber:D4}.png");
            await _pdfRasterizer.RasterizePageAsync(
                    document.StoredPath,
                    page.SourcePageNumber,
                    ocrImagePath,
                    OcrDpi,
                    cancellationToken)
                .ConfigureAwait(false);
            ocrDpi = OcrDpi;
        }

        var ocr = await _ocr.RecognizeAsync(ocrImagePath, cancellationToken).ConfigureAwait(false);
        var width = ocr.Width > 0 ? ocr.Width : page.Width;
        var height = ocr.Height > 0 ? ocr.Height : page.Height;

        var words = ocr.Words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text) && word.Width > 0 && word.Height > 0)
            .Select(word => new LatticeWord
            {
                Text = word.Text.Trim(),
                Confidence = Math.Clamp(word.Confidence, 0, 100),
                X = Math.Clamp(word.X / width, 0, 1),
                Y = Math.Clamp(word.Y / height, 0, 1),
                Width = Math.Clamp(word.Width / width, 0, 1),
                Height = Math.Clamp(word.Height / height, 0, 1)
            })
            .ToList();

        return new PageLattice
        {
            PageNumber = page.PageNumber,
            PixelWidth = width,
            PixelHeight = height,
            Dpi = ocrDpi,
            Source = LatticeSource.Ocr,
            Words = words
        };
    }

    private static PageLattice Empty(DocumentPage page) => new()
    {
        PageNumber = page.PageNumber,
        PixelWidth = page.Width,
        PixelHeight = page.Height,
        Dpi = page.Dpi,
        Source = LatticeSource.Ocr,
        Words = []
    };
}
