using Capture.Core.Import;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Capture.Pdf;

/// <summary>Writes a printable one-page PDF of a Capture separator sheet (see
/// <see cref="CaptureSeparatorSheet"/>). The barcodes are drawn as vector rectangles, not a bitmap, so
/// they print crisply at any printer resolution. The page is A4, but everything sits well inside the
/// margins, so it also prints unclipped on US Letter.</summary>
public static class CaptureSeparatorSheetWriter
{
    private const float PageWidth = 595f;   // A4, in points
    private const float PageHeight = 842f;

    public static void Write(Stream output, CaptureSeparatorKind kind)
    {
        var value = CaptureSeparatorSheet.ValueFor(kind);
        var title = CaptureSeparatorSheet.TitleFor(kind);

        using var document = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata
        {
            Title = $"Capture {title.ToLowerInvariant()} sheet",
            Creator = "Capture"
        });
        var canvas = document.BeginPage(PageWidth, PageHeight);

        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var greyPaint = new SKPaint { Color = new SKColor(0x55, 0x55, 0x55), IsAntialias = true };
        using var bold = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        using var regular = SKTypeface.FromFamilyName(null, SKFontStyle.Normal) ?? SKTypeface.Default;
        using var brandFont = new SKFont(bold, 18);
        using var titleFont = new SKFont(bold, 40);
        using var bodyFont = new SKFont(regular, 12);
        using var codeFont = new SKFont(regular, 11);

        // Solid bars top and bottom make the sheet easy to spot in a stack of paper.
        canvas.DrawRect(SKRect.Create(40, 40, PageWidth - 80, 14), ink);
        canvas.DrawRect(SKRect.Create(40, PageHeight - 54, PageWidth - 80, 14), ink);

        DrawCentered(canvas, "CAPTURE", 96, brandFont, greyPaint);
        DrawCentered(canvas, title.ToUpperInvariant(), 148, titleFont, textPaint);

        // QR code: robust to rotation and poor scans.
        const float qrSize = 280;
        DrawMatrix(canvas, Encode(value, BarcodeFormat.QR_CODE, 0, 0, margin: 0),
            SKRect.Create((PageWidth - qrSize) / 2, 190, qrSize, qrSize), ink);

        // Code 128 carrying the same value, as a second chance if the QR code doesn't survive.
        DrawMatrix(canvas, Encode(value, BarcodeFormat.CODE_128, 0, 0, margin: 0),
            SKRect.Create(110, 510, PageWidth - 220, 90), ink);
        DrawCentered(canvas, value, 618, codeFont, greyPaint);

        var instructions = kind == CaptureSeparatorKind.Batch
            ? new[]
            {
                "Place this sheet in front of the first page of each batch.",
                "In the capture profile, add a \"Capture separator sheet\" rule set to \"Batch separator\"",
                "under the batch's Starts a new batch rules."
            }
            : new[]
            {
                "Place this sheet in front of the first page of each document.",
                "In the capture profile, add a \"Capture separator sheet\" rule set to \"Document separator\"",
                "under the document type's Detect first page of a new document rules."
            };
        var y = 672f;
        foreach (var line in instructions)
        {
            DrawCentered(canvas, line, y, bodyFont, textPaint);
            y += 18;
        }
        DrawCentered(canvas, "Choose \"Remove the matching page\" to keep this sheet out of the stored document.", y + 8, bodyFont, greyPaint);

        document.EndPage();
        document.Close();
    }

    private static BitMatrix Encode(string value, BarcodeFormat format, int width, int height, int margin) =>
        new MultiFormatWriter().encode(value, format, width, height,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = margin });

    /// <summary>Draws the dark modules scaled into <paramref name="bounds"/> as one filled path (runs of
    /// adjacent modules merged per row). A single fill, rather than a rectangle per module, keeps viewers
    /// and printers from rendering anti-aliased seams between neighbouring modules.</summary>
    private static void DrawMatrix(SKCanvas canvas, BitMatrix matrix, SKRect bounds, SKPaint ink)
    {
        var moduleWidth = bounds.Width / matrix.Width;
        var moduleHeight = bounds.Height / matrix.Height;
        using var path = new SKPath { FillType = SKPathFillType.Winding };
        for (var row = 0; row < matrix.Height; row++)
        {
            var col = 0;
            while (col < matrix.Width)
            {
                if (!matrix[col, row]) { col++; continue; }
                var start = col;
                while (col < matrix.Width && matrix[col, row]) col++;
                path.AddRect(SKRect.Create(
                    bounds.Left + start * moduleWidth,
                    bounds.Top + row * moduleHeight,
                    (col - start) * moduleWidth,
                    moduleHeight));
            }
        }
        canvas.DrawPath(path, ink);
    }

    private static void DrawCentered(SKCanvas canvas, string text, float baseline, SKFont font, SKPaint paint)
    {
        var width = font.MeasureText(text);
        canvas.DrawText(text, (PageWidth - width) / 2, baseline, SKTextAlign.Left, font, paint);
    }
}
