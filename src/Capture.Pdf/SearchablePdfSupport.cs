using System.Reflection;
using Capture.Core.Lattice;
using Capture.Core.Redaction;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Capture.Pdf;

/// <summary>Shared building blocks for producing PDF/A-shaped and/or searchable (invisible OCR text
/// layer) PDF output — used by both <see cref="PdfExportAttachmentProcessor"/> (per-export attachment
/// processing) and <see cref="PdfPigExportWriter"/> (the ad hoc "Export selected to PDF" action), so the
/// two entry points into this feature share one implementation instead of drifting apart.</summary>
internal static class SearchablePdfSupport
{
    // SIL OFL-licensed, already bundled and embeddable — see src/Capture.App/Assets/Fonts/IBM-Plex-LICENSE.txt.
    // Needed for both the invisible text layer (a real glyph outline, even though never painted) and
    // PDF/A conformance (which requires every font actually used to be embedded, not a Standard14 alias).
    private const string EmbeddedFontResourceName = "Capture.Pdf.Resources.IBMPlexSans-Regular.ttf";
    public static readonly Lazy<byte[]> EmbeddedFont = new(LoadEmbeddedFont);

    /// <summary>Overlays one invisible (rendering mode "Neither" — PDF spec mode 3) text run per OCR
    /// word, positioned over its own source pixels, so the exported PDF becomes searchable/copy-
    /// pasteable without changing anything visible. Words falling inside a confirmed redaction candidate
    /// (when supplied) are skipped, so this can never resurface text a redacted copy burned out.</summary>
    public static void EmbedInvisibleWords(
        PdfPageBuilder pdfPage,
        PdfDocumentBuilder.AddedFont font,
        PageLattice lattice,
        double pageWidthPoints,
        double pageHeightPoints,
        IReadOnlyList<RedactionCandidate>? confirmedRedactions = null)
    {
        if (lattice.PixelWidth <= 0 || lattice.PixelHeight <= 0)
            return;

        var redactions = confirmedRedactions ?? [];

        // Scale by the lattice's own pixel dimensions rather than assuming it shares the page's DPI —
        // OCR can run at a different resolution than the stored page image.
        var scaleX = pageWidthPoints / lattice.PixelWidth;
        var scaleY = pageHeightPoints / lattice.PixelHeight;

        pdfPage.SetTextRenderingMode(TextRenderingMode.Neither);

        foreach (var word in lattice.Words)
        {
            if (string.IsNullOrWhiteSpace(word.Text) || word.Width <= 0 || word.Height <= 0)
                continue;
            if (IsRedacted(word, lattice.PixelWidth, lattice.PixelHeight, redactions))
                continue;

            var wordWidthPoints = word.Width * scaleX;
            var wordHeightPoints = word.Height * scaleY;
            var xPoints = word.X * scaleX;
            // Lattice Y is top-down pixel space; PDF space is bottom-up — and AddText positions by the
            // text baseline, so approximate it as the bottom edge of the word's own box.
            var baselineYPoints = pageHeightPoints - (word.Y * scaleY) - wordHeightPoints;

            var fontSize = Math.Max(1.0, wordHeightPoints);
            var probe = pdfPage.MeasureText(word.Text, fontSize, new PdfPoint(0, 0), font);
            var naturalWidth = probe.Sum(letter => letter.Width);
            var adjustedFontSize = naturalWidth > 0 ? fontSize * (wordWidthPoints / naturalWidth) : fontSize;

            pdfPage.AddText(word.Text, adjustedFontSize, new PdfPoint(xPoints, baselineYPoints), font);
        }
    }

    private static bool IsRedacted(
        LatticeWord word, int pixelWidth, int pixelHeight, IReadOnlyList<RedactionCandidate> confirmedRedactions)
    {
        if (confirmedRedactions.Count == 0)
            return false;

        var normX = word.X / pixelWidth;
        var normY = word.Y / pixelHeight;
        var normWidth = word.Width / pixelWidth;
        var normHeight = word.Height / pixelHeight;

        return confirmedRedactions.Any(candidate =>
            normX < candidate.X + candidate.Width && candidate.X < normX + normWidth
            && normY < candidate.Y + candidate.Height && candidate.Y < normY + normHeight);
    }

    private static byte[] LoadEmbeddedFont()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedFontResourceName)
            ?? throw new InvalidOperationException($"Embedded font resource '{EmbeddedFontResourceName}' not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
