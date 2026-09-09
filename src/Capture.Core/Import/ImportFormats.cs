namespace Capture.Core.Import;

public static class ImportFormats
{
    public static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    public static IEnumerable<string> AllExtensions => PdfExtensions.Concat(ImageExtensions);

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return PdfExtensions.Contains(extension) || ImageExtensions.Contains(extension);
    }

    public static bool IsPdf(string path) => PdfExtensions.Contains(Path.GetExtension(path));
}
