using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

internal static class ExportSourceFile
{
    public static string Resolve(ExportDefinition definition, CaptureDocument document)
    {
        if (definition.FileMode is ExportFileMode.None or ExportFileMode.Original)
            return document.StoredPath;

        var hasAppliedRedactedFile = document.RedactionStatus == RedactionStatus.Applied
            && !string.IsNullOrWhiteSpace(document.RedactedPath)
            && File.Exists(document.RedactedPath);
        if (hasAppliedRedactedFile)
            return document.RedactedPath!;

        if (definition.FileMode == ExportFileMode.Redacted)
            throw new InvalidOperationException(
                "Redacted export requested, but this document has no successfully applied redacted file.");

        return document.StoredPath;
    }
}
