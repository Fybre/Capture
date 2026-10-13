namespace Capture.Core.Lattice;

public enum LatticeSource
{
    PdfText = 0,
    Ocr = 1,

    /// <summary>Read by an external OCR service (Azure Document Intelligence) rather than the built-in engine.</summary>
    ExternalOcr = 2
}

public sealed class LatticeWord
{
    public string Text { get; init; } = string.Empty;
    public float Confidence { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
}

public sealed class PageLattice
{
    public int PageNumber { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public int Dpi { get; init; }
    public LatticeSource Source { get; init; }
    public IReadOnlyList<LatticeWord> Words { get; init; } = [];

    /// <summary>A problem reading this page that a reviewer should know about — e.g. external OCR failed
    /// and the built-in engine's text was used instead.</summary>
    public string? Warning { get; init; }
}

public sealed class OcrWord
{
    public string Text { get; init; } = string.Empty;
    public float Confidence { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
}

public sealed class OcrResult
{
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyList<OcrWord> Words { get; init; } = [];
}
