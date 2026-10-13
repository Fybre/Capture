using Capture.Core.CaptureProfiles;
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
    private readonly IExternalOcrEngine? _externalOcr;
    private readonly IOcrOptionsResolver? _ocrOptions;

    public LatticeBuilder(
        IAppPaths paths,
        ILatticeStore store,
        IPdfTextExtractor pdfText,
        IOcrEngine ocr,
        IPdfRasterizer pdfRasterizer,
        IExternalOcrEngine? externalOcr = null,
        IOcrOptionsResolver? ocrOptions = null)
    {
        _externalOcr = externalOcr;
        _ocrOptions = ocrOptions;
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
        // Each page's OCR/lattice build is independent of every other page in the document — OCR spawns
        // its own Tesseract process per page, so building pages one at a time wastes every core beyond
        // the first on a multi-page document. Saves still happen per page (each to its own file), so
        // there's no shared state to protect beyond the bounded degree of parallelism itself.
        var ocr = _ocrOptions is null ? OcrOptions.BuiltInOnly : await _ocrOptions.ResolveAsync(document, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            pages,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (page, ct) =>
            {
                try
                {
                    var lattice = await BuildPageAsync(document, page, ct, ocr).ConfigureAwait(false);
                    await _store.SaveAsync(document.Id, lattice, ct).ConfigureAwait(false);
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
                    await _store.SaveAsync(document.Id, Empty(page), ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    public async Task<PageLattice> BuildPageAsync(
        CaptureDocument document,
        DocumentPage page,
        CancellationToken cancellationToken = default,
        OcrOptions? ocr = null)
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

        ocr ??= _ocrOptions is null ? OcrOptions.BuiltInOnly : await _ocrOptions.ResolveAsync(document, cancellationToken).ConfigureAwait(false);
        var external = ocr.Mode == ExternalOcrMode.Off ? null : _externalOcr;
        string? warning = null;

        if (external is not null && ocr.Mode == ExternalOcrMode.AllPages)
        {
            try
            {
                var read = await external.RecognizeAsync(ocr.ConnectionId, ocrImagePath, cancellationToken).ConfigureAwait(false);
                return ToLattice(read, page, ocrDpi, LatticeSource.ExternalOcr, null);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceError($"External OCR failed for page {page.PageNumber}: {ex}");
                warning = $"Page {page.PageNumber}: Azure OCR failed, so the built-in OCR was used — {ex.Message}";
            }
        }

        var builtIn = ToLattice(await _ocr.RecognizeAsync(ocrImagePath, cancellationToken).ConfigureAwait(false), page, ocrDpi, LatticeSource.Ocr, warning);
        if (external is null || ocr.Mode != ExternalOcrMode.WhenPoor || !LatticeQuality.IsPoorOcr(builtIn.Words, ocr.MinConfidence))
            return builtIn;

        try
        {
            var read = await external.RecognizeAsync(ocr.ConnectionId, ocrImagePath, cancellationToken).ConfigureAwait(false);
            return ToLattice(read, page, ocrDpi, LatticeSource.ExternalOcr, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.TraceError($"External OCR failed for page {page.PageNumber}: {ex}");
            return ToLattice(builtIn, $"Page {page.PageNumber}: the built-in OCR read this page poorly and Azure OCR failed — {ex.Message}");
        }
    }

    private static PageLattice ToLattice(OcrResult ocr, DocumentPage page, int dpi, LatticeSource source, string? warning)
    {
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
            Dpi = dpi,
            Source = source,
            Words = words,
            Warning = warning
        };
    }

    private static PageLattice ToLattice(PageLattice lattice, string warning) => new()
    {
        PageNumber = lattice.PageNumber,
        PixelWidth = lattice.PixelWidth,
        PixelHeight = lattice.PixelHeight,
        Dpi = lattice.Dpi,
        Source = lattice.Source,
        Words = lattice.Words,
        Warning = warning
    };

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
