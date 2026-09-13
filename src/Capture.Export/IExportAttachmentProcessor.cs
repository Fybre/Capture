using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

/// <summary>Resolves the file an export writer should attach/copy, applying
/// <see cref="ExportDefinition.Pdfa"/> and <see cref="ExportDefinition.SearchablePdf"/> on top of
/// <see cref="ExportSourceFile.Resolve"/>'s plain original/redacted choice. Every <see cref="IExportWriter"/>
/// that attaches a file should call this instead of <see cref="ExportSourceFile.Resolve"/> directly.</summary>
public interface IExportAttachmentProcessor
{
    /// <summary>Returns the path to attach. When neither flag is set, this is exactly
    /// <see cref="ExportSourceFile.Resolve"/>'s own result — no new file is written. Otherwise returns a
    /// newly built temporary PDF; callers don't need to delete it themselves (see
    /// <see cref="CleanupAsync"/>).</summary>
    Task<string> ResolveAsync(
        ExportDefinition definition, CaptureDocument document, CancellationToken cancellationToken = default);

    /// <summary>Deletes any temporary file(s) this processor produced for this document, if any. Safe to
    /// call even when nothing was produced (e.g. neither flag was set for any of the document's exports).</summary>
    Task CleanupAsync(Guid documentId, CancellationToken cancellationToken = default);
}
