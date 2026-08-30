using System.Diagnostics;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

/// <summary>Runs every enabled <see cref="ExportDefinition"/> on a profile against one document —
/// the single orchestration point behind the app's manual Export action, analogous to how
/// <c>RedactionApplier</c> is the single orchestration point for redaction.</summary>
public sealed class ProfileExportRunner
{
    private readonly IReadOnlyDictionary<ExportType, IExportWriter> _writers;

    public ProfileExportRunner(IEnumerable<IExportWriter> writers)
    {
        _writers = writers.ToDictionary(writer => writer.Type);
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(
        IndexingProfile profile,
        CaptureDocument document,
        IReadOnlyList<IndexValue> indexValues,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ExportResult>();
        foreach (var definition in profile.Exports.Where(item => item.Enabled))
        {
            if (!_writers.TryGetValue(definition.Type, out var writer))
            {
                results.Add(new ExportResult(false, $"\"{definition.Name}\": no exporter registered for {definition.Type}"));
                continue;
            }

            Trace.TraceInformation($"Export \"{definition.Name}\" ({definition.Type}) starting for document {document.Id}");
            var context = new ExportDocumentContext(document, profile.Fields, indexValues);
            var result = await writer.ExportAsync(definition, context, cancellationToken).ConfigureAwait(false);
            Trace.TraceInformation(result.Success
                ? $"Export \"{definition.Name}\" succeeded for document {document.Id}: {result.Message}"
                : $"Export \"{definition.Name}\" failed for document {document.Id}: {result.Message}");
            results.Add(result);
        }

        return results;
    }
}
