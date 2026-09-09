namespace Capture.Core.Lattice;

public enum LatticeSource
{
    PdfText = 0,
    Ocr = 1
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
