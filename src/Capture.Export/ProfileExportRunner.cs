using System.Diagnostics;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Scripting;

namespace Capture.Export;

/// <summary>Runs every enabled <see cref="ExportDefinition"/> on a profile against one document —
/// the single orchestration point behind the app's manual Export action, analogous to how
/// <c>RedactionApplier</c> is the single orchestration point for redaction.</summary>
public sealed class ProfileExportRunner
{
    private readonly IReadOnlyDictionary<ExportType, IExportWriter> _writers;
    private readonly IFieldScriptRunner? _scripts;

    public ProfileExportRunner(IEnumerable<IExportWriter> writers, IFieldScriptRunner? scripts = null)
    {
        _writers = writers.ToDictionary(writer => writer.Type);
        _scripts = scripts;
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(
        IndexingProfile profile,
        CaptureDocument document,
        IReadOnlyList<IndexValue> indexValues,
        CancellationToken cancellationToken = default)
    {
        // BeforeExport/AfterExport scripts operate on a clone, never the caller's own list — mutating
        // a value here reshapes only what gets written out, not the stored/reviewed document (unlike
        // ProfileApplicator's AfterFieldsPopulated scripts, which legitimately mutate persisted data).
        var snapshot = indexValues.Select(Clone).ToList();
        await RunScriptsAsync(profile, document, snapshot, ScriptTrigger.BeforeExport, cancellationToken).ConfigureAwait(false);

        var results = new List<ExportResult>();
        foreach (var definition in profile.Exports.Where(item => item.Enabled))
        {
            if (!_writers.TryGetValue(definition.Type, out var writer))
            {
                results.Add(new ExportResult(false, $"\"{definition.Name}\": no exporter registered for {definition.Type}"));
                continue;
            }

            Trace.TraceInformation($"Export \"{definition.Name}\" ({definition.Type}) starting for document {document.Id}");
            var context = new ExportDocumentContext(document, profile.Fields, snapshot);
            var result = await writer.ExportAsync(definition, context, cancellationToken).ConfigureAwait(false);
            Trace.TraceInformation(result.Success
                ? $"Export \"{definition.Name}\" succeeded for document {document.Id}: {result.Message}"
                : $"Export \"{definition.Name}\" failed for document {document.Id}: {result.Message}");
            results.Add(result);
        }

        // AfterExport scripts are side-effect-only (a webhook, an audit log entry) — any field write
        // here is discarded along with the rest of this method's local `snapshot`.
        await RunScriptsAsync(profile, document, snapshot, ScriptTrigger.AfterExport, cancellationToken).ConfigureAwait(false);

        return results;
    }

    private async Task RunScriptsAsync(IndexingProfile profile, CaptureDocument document, List<IndexValue> values, ScriptTrigger trigger, CancellationToken cancellationToken)
    {
        if (_scripts is null || !_scripts.IsAvailable)
            return;

        var scripts = profile.Scripts.Where(script => script.Enabled && script.Trigger == trigger && !string.IsNullOrEmpty(script.Source));
        if (!scripts.Any())
            return;

        var context = new ScriptExecutionContext
        {
            ProfileName = profile.Name,
            DocumentNumber = 1,
            BatchNumber = 1,
            Timestamp = DateTimeOffset.Now,
            Values = values,
            // Full extracted text isn't available at export time (no PageLattice access here) — only
            // at AfterFieldsPopulated. FileName/extension/page count still come from the real document;
            // ScriptDocumentInfo.From([], document) leaves Text empty for a lattice-less caller.
            Document = ScriptDocumentInfo.From([], document)
        };

        foreach (var script in scripts)
        {
            var result = await _scripts.RunProfileScriptAsync(script, context, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                Trace.TraceError($"Export script \"{script.Name}\" ({trigger}) failed: {result.ErrorMessage}");
        }
    }

    private static IndexValue Clone(IndexValue source) => new()
    {
        FieldId = source.FieldId,
        FieldName = source.FieldName,
        Format = source.Format,
        Level = source.Level,
        Mandatory = source.Mandatory,
        Value = source.Value,
        Confidence = source.Confidence,
        IsManual = source.IsManual,
        PageNumber = source.PageNumber,
        Bounds = source.Bounds,
        ValidationError = source.ValidationError,
        HideFromIndexing = source.HideFromIndexing,
        IsReadOnly = source.IsReadOnly,
        Sensitive = source.Sensitive,
        Kind = source.Kind,
        LookupOptions = source.LookupOptions
    };
}
