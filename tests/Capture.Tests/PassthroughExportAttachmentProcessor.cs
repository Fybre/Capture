using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Export;

namespace Capture.Tests;

/// <summary>Test double for writer tests that don't exercise Pdfa/SearchablePdf — behaves exactly
/// like a no-op processor (plain <see cref="ExportSourceFile.Resolve"/>, nothing to clean up).</summary>
public sealed class PassthroughExportAttachmentProcessor : IExportAttachmentProcessor
{
    public Task<string> ResolveAsync(ExportDefinition definition, CaptureDocument document, CancellationToken cancellationToken = default) =>
        Task.FromResult(ExportSourceFile.Resolve(definition, document));

    public Task CleanupAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
