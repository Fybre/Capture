using System.Diagnostics;
using Capture.Core.CaptureProfiles;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Scripting;

namespace Capture.Core.Indexing;

public sealed class ProfileApplicator : IProfileApplicator
{
    private readonly IBarcodeDecoder? _barcodes;
    private readonly IAiExtractor? _ai;
    private readonly IFieldScriptRunner? _scripts;

    public ProfileApplicator(IBarcodeDecoder? barcodes = null, IAiExtractor? ai = null, IFieldScriptRunner? scripts = null)
    {
        _barcodes = barcodes;
        _ai = ai;
        _scripts = scripts;
    }

    public IReadOnlyList<IndexValue> Apply(
        DocumentTypeDefinition documentType,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null) =>
        Apply(documentType.Fields, lattices, documentType.Name, documentType.Locale, context, pages, batchSeparatorValue, existingValues, document);

    public Task<IReadOnlyList<IndexValue>> ApplyAsync(
        DocumentTypeDefinition documentType,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(documentType.Fields, documentType.Scripts, documentType.SharedScriptSource, lattices,
            documentType.Name, documentType.Locale, context, pages, batchSeparatorValue, existingValues, document, cancellationToken);

    public IReadOnlyList<IndexValue> Apply(
        IReadOnlyList<IndexField> fields,
        IReadOnlyList<PageLattice> lattices,
        string? profileName = null,
        string? locale = null,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null)
    {
        var results = ExtractAll(fields, locale, lattices, pages, batchSeparatorValue);
        ApplyBoundaryValues(fields, results, existingValues, locale);
        ApplyDefaults(fields, profileName, locale, results, context, existingValues, pages?.Count ?? lattices.Count);
        return results;
    }

    public async Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IReadOnlyList<IndexField> fields,
        IReadOnlyList<FieldScript> scripts,
        string sharedScriptSource,
        IReadOnlyList<PageLattice> lattices,
        string? profileName = null,
        string? locale = null,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null,
        CancellationToken cancellationToken = default)
    {
        var results = ExtractAll(fields, locale, lattices, pages, batchSeparatorValue);
        ApplyBoundaryValues(fields, results, existingValues, locale);
        await FillAiAsync(fields, locale, lattices, results, cancellationToken).ConfigureAwait(false);
        await FillFieldScriptsAsync(fields, sharedScriptSource, profileName, locale, results, lattices, context, document, cancellationToken).ConfigureAwait(false);
        await RunProfileScriptsAsync(fields, scripts, sharedScriptSource, profileName, locale, results, lattices, context, document, ScriptTrigger.AfterFieldsPopulated, cancellationToken).ConfigureAwait(false);
        ApplyDefaults(fields, profileName, locale, results, context, existingValues, pages?.Count ?? lattices.Count);
        await ApplyPostProcessScriptsAsync(fields, sharedScriptSource, profileName, locale, results, lattices, context, document, cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static void ApplyBoundaryValues(
        IReadOnlyList<IndexField> fields,
        List<IndexValue> results,
        IReadOnlyList<IndexValue>? existingValues,
        string? locale)
    {
        if (existingValues is null) return;
        foreach (var field in fields.Where(item => item.BoundaryRuleId is not null))
        {
            var captured = existingValues.FirstOrDefault(item => item.FieldId == field.Id);
            var result = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (captured is null || result is null) continue;
            result.Value = captured.Value;
            result.Confidence = captured.Confidence;
            result.ValidationError = IndexFormat.Validate(result.Value, field.Format, locale);
        }
    }

    private List<IndexValue> ExtractAll(
        IReadOnlyList<IndexField> fields,
        string? locale,
        IReadOnlyList<PageLattice> lattices,
        IReadOnlyList<DocumentPage>? pages,
        string? batchSeparatorValue)
    {
        var results = new List<IndexValue>(fields.Count);
        foreach (var field in fields)
        {
            var extracted = Extract(field, lattices, pages, batchSeparatorValue);
            extracted.ValidationError = IndexFormat.Validate(extracted.Value, field.Format, locale);
            results.Add(extracted);
        }

        return results;
    }

    private async Task FillAiAsync(
        IReadOnlyList<IndexField> fields,
        string? locale,
        IReadOnlyList<PageLattice> lattices,
        List<IndexValue> results,
        CancellationToken cancellationToken)
    {
        var aiFields = fields.Where(field => field.Kind == FieldKind.Ai).ToList();
        if (aiFields.Count == 0 || _ai is null || !_ai.IsConfigured)
            return;

        var extracted = await _ai.ExtractAsync(DocumentText.FromLattices(lattices), aiFields, cancellationToken)
            .ConfigureAwait(false);
        foreach (var field in aiFields)
        {
            if (!extracted.TryGetValue(field.Id, out var hit))
                continue;
            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is null)
                continue;
            value.Value = hit.Value;
            value.Confidence = hit.Confidence;
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, locale);
        }
    }

    // Evaluates each Script-kind field's ScriptExpression in field-list order — deliberately
    // sequential (not simultaneous, unlike Text/Lookup templates below), so a later Script field can
    // reference an earlier one's already-resolved value. Read-only over every field (see
    // ReadOnlyScriptGlobals) — a field expression can only ever change its own value.
    private async Task FillFieldScriptsAsync(
        IReadOnlyList<IndexField> fields,
        string sharedScriptSource,
        string? profileName,
        string? locale,
        List<IndexValue> results,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context,
        CaptureDocument? document,
        CancellationToken cancellationToken)
    {
        if (_scripts is null || !_scripts.IsAvailable)
            return;

        var scriptFields = fields.Where(field => field.Kind == FieldKind.Script && !string.IsNullOrEmpty(field.ScriptExpression));
        if (!scriptFields.Any())
            return;

        var execContext = BuildExecutionContext(profileName, results, lattices, context, document);
        foreach (var field in scriptFields)
        {
            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is null || value.IsManual)
                continue;

            var result = await _scripts.RunFieldExpressionAsync(field.Id, field.ScriptExpression!, execContext, cancellationToken, sharedScriptSource)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                Trace.TraceError($"Field script \"{field.Name}\" failed: {result.ErrorMessage}");
                value.ValidationError = result.ErrorMessage;
                continue;
            }

            value.Value = result.Value ?? string.Empty;
            value.Confidence = result.Confidence ?? 100;
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, locale);
        }
    }

    // Deliberately the very last pipeline step (after ApplyDefaults) so a field's PostProcessScript
    // always sees that field's real final pre-cleanup value regardless of Kind — a Zonal/KeyValue/
    // Regex/Barcode/AI field's freshly re-extracted result, or a Text/Lookup field's resolved default
    // template / preserved manual edit. The tradeoff: an earlier Script-kind field expression or
    // profile-level script that reads this field's value (both of which run before ApplyDefaults) sees
    // the pre-cleanup text, not the cleaned-up result — cleanup is a final display/export-facing pass,
    // not an input to the rest of the extraction pipeline. Cleanup preserves the input confidence by
    // default; the expression can explicitly replace it with SetConfidence(...).
    private async Task ApplyPostProcessScriptsAsync(
        IReadOnlyList<IndexField> fields,
        string sharedScriptSource,
        string? profileName,
        string? locale,
        List<IndexValue> results,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context,
        CaptureDocument? document,
        CancellationToken cancellationToken)
    {
        if (_scripts is null || !_scripts.IsAvailable)
            return;

        var postProcessFields = fields
            .Where(field => field.Kind is not (FieldKind.Script or FieldKind.Button)
                && !string.IsNullOrEmpty(field.PostProcessScript))
            .ToList();
        if (postProcessFields.Count == 0)
            return;

        var execContext = BuildExecutionContext(profileName, results, lattices, context, document);
        foreach (var field in postProcessFields)
        {
            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is null || value.IsManual)
                continue;

            var result = await _scripts.RunFieldExpressionAsync(field.Id, field.PostProcessScript!, execContext, cancellationToken, sharedScriptSource)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                Trace.TraceError($"Post-process script \"{field.Name}\" failed: {result.ErrorMessage}");
                continue;
            }

            value.Value = result.Value ?? string.Empty;
            if (result.Confidence is { } confidence)
                value.Confidence = confidence;
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, locale);
        }
    }

    // Runs every enabled profile-level script for the given trigger, in list order — later scripts see
    // earlier scripts' mutations, same sequential-pipeline semantics as FillFieldScriptsAsync above. A
    // throwing/timed-out script (surfaced as a failed ScriptRunResult, never a thrown exception —
    // RoslynFieldScriptRunner never lets one cross this boundary) is logged and skipped; it never
    // aborts the document, matching every other pipeline step's failure contract.
    private async Task RunProfileScriptsAsync(
        IReadOnlyList<IndexField> fields,
        IReadOnlyList<FieldScript> scripts,
        string sharedScriptSource,
        string? profileName,
        string? locale,
        List<IndexValue> results,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context,
        CaptureDocument? document,
        ScriptTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (_scripts is null || !_scripts.IsAvailable)
            return;

        var enabledScripts = scripts.Where(script => script.Enabled && script.Trigger == trigger && !string.IsNullOrEmpty(script.Source)).ToList();
        if (enabledScripts.Count == 0)
            return;

        var execContext = BuildExecutionContext(profileName, results, lattices, context, document);
        foreach (var script in enabledScripts)
        {
            var result = await _scripts.RunProfileScriptAsync(script, execContext, cancellationToken, sharedScriptSource).ConfigureAwait(false);
            if (!result.Success)
                Trace.TraceError($"Script \"{script.Name}\" failed: {result.ErrorMessage}");
        }

        // A script may have touched any field — recompute validation for all of them rather than
        // trying to track which ones actually changed.
        foreach (var value in results)
        {
            var field = fields.FirstOrDefault(item => item.Id == value.FieldId);
            if (field is not null)
                value.ValidationError = IndexFormat.Validate(value.Value, field.Format, locale);
        }
    }

    private static ScriptExecutionContext BuildExecutionContext(
        string? profileName,
        List<IndexValue> results,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context,
        CaptureDocument? document) => new()
    {
        ProfileName = profileName ?? string.Empty,
        DocumentNumber = context?.DocumentNumber ?? 1,
        BatchNumber = context?.BatchNumber ?? 1,
        Timestamp = context?.Timestamp ?? DateTimeOffset.Now,
        Values = results,
        Document = ScriptDocumentInfo.From(lattices, document),
        Scope = context?.ScriptScope ?? (document is null ? ScriptScopeKind.Batch : ScriptScopeKind.Document),
        DocumentType = context?.DocumentType ?? profileName,
        TriggerMatches = context?.TriggerMatches ?? [],
        BatchValues = context?.BatchValues
    };

    private static void ApplyDefaults(
        IReadOnlyList<IndexField> fields,
        string? profileName,
        string? locale,
        List<IndexValue> results,
        DefaultValueContext? context,
        IReadOnlyList<IndexValue>? existingValues,
        int pageCount)
    {
        // A profile re-application re-extracts generated fields, but Text/Lookup/Script fields are
        // entered, chosen, or computed for the indexer to review. Preserve manual edits whether or not
        // the field also has a computed default — otherwise a manual override (including one overriding
        // a script's own output) would get silently reset on the next reprocess. This is also the
        // structural guarantee that a profile-level script (which runs earlier, in FillFieldScriptsAsync/
        // RunProfileScriptsAsync above) can never permanently clobber a manually-entered value.
        foreach (var field in fields.Where(field => field.Kind is FieldKind.Text or FieldKind.Lookup or FieldKind.Script or FieldKind.Button))
        {
            var existing = existingValues?.FirstOrDefault(item => item.FieldId == field.Id);
            if (existing is not { IsManual: true })
                continue;

            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is null)
                continue;

            value.Value = existing.Value;
            value.IsManual = true;
            value.Confidence = existing.Confidence;
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, locale);
        }

        var templatedFieldIds = fields
            .Where(field =>
                (field.Kind is not (FieldKind.Script or FieldKind.Button or FieldKind.BatchSeparatorValue or FieldKind.Lookup) &&
                 !string.IsNullOrEmpty(field.DefaultValueTemplate)) ||
                (field.Kind == FieldKind.Lookup && !string.IsNullOrEmpty(field.LookupKeyTemplate)))
            .Select(field => field.Id)
            .ToHashSet();
        if (templatedFieldIds.Count == 0)
            return;

        // Start with caller-supplied and batch values so document fields can use shared batch indexes.
        // Current-scope extraction wins when a batch and document field happen to share a name.
        var fieldValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in context?.Fields ?? new Dictionary<string, string>())
            fieldValues[item.Key] = item.Value;
        foreach (var item in context?.BatchValues ?? [])
            fieldValues[item.FieldName] = item.Value ?? string.Empty;
        foreach (var item in results)
            fieldValues[item.FieldName] = item.Value ?? string.Empty;

        var evalContext = new DefaultValueContext
        {
            DocumentNumber = context?.DocumentNumber ?? 1,
            BatchNumber = context?.BatchNumber ?? 1,
            PageCount = Math.Max(1, context is { PageCount: > 0 } ? context.PageCount : pageCount),
            Timestamp = context?.Timestamp ?? DateTimeOffset.Now,
            ProfileName = profileName ?? string.Empty,
            Fields = fieldValues
        };

        var templatedFields = fields.Where(field => templatedFieldIds.Contains(field.Id)).ToList();
        var templatedByName = templatedFields
            .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resolving = new HashSet<Guid>();
        var resolved = new HashSet<Guid>();

        bool Resolve(IndexField field)
        {
            if (resolved.Contains(field.Id))
                return true;

            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is null || value.IsManual)
            {
                resolved.Add(field.Id);
                return true;
            }

            if (field.Kind != FieldKind.Lookup && !string.IsNullOrWhiteSpace(value.Value))
            {
                fieldValues[field.Name] = value.Value;
                resolved.Add(field.Id);
                return true;
            }

            if (!resolving.Add(field.Id))
                return false;

            var template = field.Kind == FieldKind.Lookup ? field.LookupKeyTemplate : field.DefaultValueTemplate;
            foreach (var reference in DefaultValueTemplateEvaluator.ReferencedFields(template))
            {
                if (templatedByName.TryGetValue(reference, out var dependency) && !Resolve(dependency))
                {
                    resolving.Remove(field.Id);
                    return false;
                }
            }

            var fieldContext = new DefaultValueContext
            {
                DocumentNumber = evalContext.DocumentNumber,
                BatchNumber = evalContext.BatchNumber,
                PageNumber = Math.Max(1, value.PageNumber > 0 ? value.PageNumber : field.PageNumber),
                PageCount = evalContext.PageCount,
                Timestamp = evalContext.Timestamp,
                ProfileName = evalContext.ProfileName,
                Fields = evalContext.Fields
            };

            if (field.Kind == FieldKind.Lookup)
            {
                ApplyLookupKeyTemplate(field, value, fieldContext, locale);
                fieldValues[field.Name] = value.Value;
                resolving.Remove(field.Id);
                resolved.Add(field.Id);
                return true;
            }

            if (!DefaultValueTemplateEvaluator.TryEvaluate(
                    field.DefaultValueTemplate,
                    fieldContext,
                    out var evaluated,
                    out var templateError))
            {
                value.Value = string.Empty;
                value.Confidence = 0;
                value.ValidationError = templateError;
                resolving.Remove(field.Id);
                resolved.Add(field.Id);
                return true;
            }

            value.Value = evaluated;
            value.Confidence = 100;
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, locale);
            fieldValues[field.Name] = value.Value;
            resolving.Remove(field.Id);
            resolved.Add(field.Id);
            return true;
        }

        foreach (var field in templatedFields)
        {
            if (Resolve(field))
                continue;
            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is not null)
                value.ValidationError = "Circular value source";
        }
    }

    // Resolves LookupKeyTemplate (same token syntax as a Text field's DefaultValueTemplate) and
    // matches the result case-insensitively against this field's LookupOptions keys. No match (or a
    // template that resolves blank, e.g. the referenced field hasn't been extracted yet) leaves
    // `value` exactly as Extract() left it — the static LookupDefaultValue fallback, or blank.
    private static void ApplyLookupKeyTemplate(
        IndexField field,
        IndexValue value,
        DefaultValueContext evalContext,
        string? locale)
    {
        if (!DefaultValueTemplateEvaluator.TryEvaluate(
                field.LookupKeyTemplate,
                evalContext,
                out var resolvedKey,
                out var templateError))
        {
            value.ValidationError = templateError;
            return;
        }

        resolvedKey = resolvedKey.Trim();
        if (resolvedKey.Length == 0)
            return;

        var match = field.LookupOptions.FirstOrDefault(
            option => string.Equals(option.Key.Trim(), resolvedKey, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return;

        value.Value = match.Value;
        value.Confidence = 100;
        value.ValidationError = IndexFormat.Validate(value.Value, field.Format, locale);
    }

    private IndexValue Extract(
        IndexField field,
        IReadOnlyList<PageLattice> lattices,
        IReadOnlyList<DocumentPage>? pages,
        string? batchSeparatorValue = null)
    {
        var value = new IndexValue
        {
            FieldId = field.Id,
            FieldName = field.Name,
            Format = field.Format,
            Mandatory = field.Mandatory,
            HideFromIndexing = field.HideFromIndexing,
            IsReadOnly = field.IsReadOnly,
            Sensitive = field.Sensitive,
            Kind = field.Kind,
            LookupOptions = field.LookupOptions.Select(CloneLookupOption).ToList(),
            ButtonLabel = field.ButtonLabel,
            PageNumber = field.PageNumber
        };

        if (field.Kind == FieldKind.BatchSeparatorValue)
        {
            if (!string.IsNullOrEmpty(batchSeparatorValue))
            {
                value.Value = batchSeparatorValue;
                value.Confidence = 100;
            }

            return value;
        }

        if (field.Kind == FieldKind.Lookup
            && field.LookupDefaultValue is { } defaultValue
            && field.LookupOptions.Any(option => string.Equals(option.Value, defaultValue, StringComparison.Ordinal)))
        {
            value.Value = defaultValue;
            value.Confidence = 100;
            return value;
        }

        if (field.Kind == FieldKind.Barcode)
            return ExtractBarcode(field, value, pages);

        if (field.Kind == FieldKind.Zonal && field.Zone is not null)
        {
            var candidates = field.PageScope switch
            {
                PageScope.First => lattices.Where(item => item.PageNumber == 1),
                PageScope.Any => lattices.OrderBy(item => item.PageNumber),
                _ => lattices.Where(item => item.PageNumber == Math.Max(1, field.PageNumber))
            };

            foreach (var page in candidates)
            {
                var zonal = ZonalExtractor.Extract(page, field.Zone);
                if (string.IsNullOrWhiteSpace(zonal.Text))
                    continue;

                value.Value = zonal.Text;
                value.Confidence = zonal.Confidence;
                value.PageNumber = page.PageNumber;
                value.Bounds = new ZoneRect
                {
                    PageNumber = page.PageNumber,
                    X = field.Zone.X,
                    Y = field.Zone.Y,
                    Width = field.Zone.Width,
                    Height = field.Zone.Height
                };
                return value;
            }

            return value;
        }

        if (field.Kind is FieldKind.KeyValue or FieldKind.Regex)
        {
            var pattern = field.Kind == FieldKind.Regex
                ? RegexExtractor.Extract(lattices, field)
                : KeyValueExtractor.Extract(lattices, field);
            value.Value = pattern.Text;
            value.Confidence = pattern.Confidence;
            value.PageNumber = pattern.PageNumber;
            value.Bounds = pattern.Bounds;
        }

        return value;
    }

    private static LookupOption CloneLookupOption(LookupOption option) => new()
    {
        Key = option.Key,
        Value = option.Value
    };

    private IndexValue ExtractBarcode(
        IndexField field,
        IndexValue value,
        IReadOnlyList<DocumentPage>? pages)
    {
        if (_barcodes is null || pages is null || pages.Count == 0)
            return value;

        var candidates = field.PageScope switch
        {
            PageScope.First => pages.Where(item => item.PageNumber == 1),
            PageScope.Any => pages.OrderBy(item => item.PageNumber),
            _ => pages.Where(item => item.PageNumber == Math.Max(1, field.Zone?.PageNumber ?? field.PageNumber))
        };

        foreach (var page in candidates)
        {
            var decoded = _barcodes.Decode(page.ImagePath, field.Zone);
            if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text) || !BarcodePatterns.Matches(field, decoded.Text))
                continue;

            value.Value = decoded.Text;
            value.Confidence = decoded.Confidence;
            value.PageNumber = page.PageNumber;
            value.Bounds = field.Zone;
            return value;
        }

        return value;
    }
}
