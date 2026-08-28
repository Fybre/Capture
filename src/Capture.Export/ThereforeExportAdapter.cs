using Capture.Core.Models;

namespace Capture.Export;

public sealed class ThereforeExportAdapter : IExportAdapter
{
    public string Id => "therefore";
    public string Name => "Therefore";
    public bool IsConfigured => false;

    public Task<ExportResult> ExportAsync(CaptureDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExportResult(false, "Therefore export is not implemented yet."));
    }
}
