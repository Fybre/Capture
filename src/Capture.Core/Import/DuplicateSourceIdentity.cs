using Capture.Core.Models;

namespace Capture.Core.Import;

/// <summary>Distinguishes documents produced from one source-file occurrence from a later import of
/// the same bytes. The hash answers "same content"; this identity answers "same import".</summary>
public static class DuplicateSourceIdentity
{
    public static string For(CaptureDocument document) =>
        document.SourceImportId?.ToString("N") ?? $"legacy:{document.ContentHash}";

    public static bool AreDuplicateImports(CaptureDocument first, CaptureDocument second) =>
        !string.IsNullOrEmpty(first.ContentHash)
        && string.Equals(first.ContentHash, second.ContentHash, StringComparison.Ordinal)
        && !string.Equals(For(first), For(second), StringComparison.Ordinal);
}
