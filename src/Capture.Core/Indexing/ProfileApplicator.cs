using System.Diagnostics;
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
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null)
    {
        var results = ExtractAll(profile, lattices, pages, batchSeparatorValue);
        ApplyDefaults(profile, results, context, existingValues);
        return results;
    }

    public async Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CancellationToken cancellationToken = default)
    {
        var results = ExtractAll(profile, lattices, pages, batchSeparatorValue);
        await FillAiAsync(profile, lattices, results, cancellationToken).ConfigureAwait(false);
        await FillFieldScriptsAsync(profile, results, context, cancellationToken).ConfigureAwait(false);
        await RunProfileScriptsAsync(profile, results, context, ScriptTrigger.AfterFieldsPopulated, cancellationToken).ConfigureAwait(false);
        ApplyDefaults(profile, results, context, existingValues);
        return results;
    }

    private List<IndexValue> ExtractAll(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        IReadOnlyList<DocumentPage>? pages,
        string? batchSeparatorValue)
    {
        var results = new List<IndexValue>(profile.Fields.Count);
        foreach (var field in profile.Fields)
        {
            var extracted = Extract(field, lattices, pages, batchSeparatorValue);
            extracted.ValidationError = IndexFormat.Validate(extracted.Value, field.Format, profile.Locale);
            results.Add(extracted);
        }

        return results;
    }

    private async Task FillAiAsync(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        List<IndexValue> results,
        CancellationToken cancellationToken)
    {
        var aiFields = profile.Fields.Where(field => field.Kind == FieldKind.Ai).ToList();
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
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, profile.Locale);
        }
    }

    // Evaluates each Script-kind field's ScriptExpression in field-list order — deliberately
    // sequential (not simultaneous, unlike Text/Lookup templates below), so a later Script field can
    // reference an earlier one's already-resolved value. Read-only over every field (see
    // ReadOnlyScriptGlobals) — a field expression can only ever change its own value.
    private async Task FillFieldScriptsAsync(
        IndexingProfile profile,
        List<IndexValue> results,
        DefaultValueContext? context,
        CancellationToken cancellationToken)
    {
        if (_scripts is null || !_scripts.IsAvailable)
            return;

        var scriptFields = profile.Fields.Where(field => field.Kind == FieldKind.Script && !string.IsNullOrEmpty(field.ScriptExpression));
        if (!scriptFields.Any())
            return;

        var execContext = BuildExecutionContext(profile, results, context);
        foreach (var field in scriptFields)
        {
            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is null || value.IsManual)
                continue;

            var result = await _scripts.RunFieldExpressionAsync(field.Id, field.ScriptExpression!, execContext, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                Trace.TraceError($"Field script \"{field.Name}\" failed: {result.ErrorMessage}");
                value.ValidationError = result.ErrorMessage;
                continue;
            }

            value.Value = result.Value ?? string.Empty;
            value.Confidence = 100;
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, profile.Locale);
        }
    }

    // Runs every enabled profile-level script for the given trigger, in list order — later scripts see
    // earlier scripts' mutations, same sequential-pipeline semantics as FillFieldScriptsAsync above. A
    // throwing/timed-out script (surfaced as a failed ScriptRunResult, never a thrown exception —
    // RoslynFieldScriptRunner never lets one cross this boundary) is logged and skipped; it never
    // aborts the document, matching every other pipeline step's failure contract.
    private async Task RunProfileScriptsAsync(
        IndexingProfile profile,
        List<IndexValue> results,
        DefaultValueContext? context,
        ScriptTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (_scripts is null || !_scripts.IsAvailable)
            return;

        var scripts = profile.Scripts.Where(script => script.Enabled && script.Trigger == trigger && !string.IsNullOrEmpty(script.Source)).ToList();
        if (scripts.Count == 0)
            return;

        var execContext = BuildExecutionContext(profile, results, context);
        foreach (var script in scripts)
        {
            var result = await _scripts.RunProfileScriptAsync(script, execContext, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                Trace.TraceError($"Script \"{script.Name}\" failed: {result.ErrorMessage}");
        }

        // A script may have touched any field — recompute validation for all of them rather than
        // trying to track which ones actually changed.
        foreach (var value in results)
        {
            var field = profile.Fields.FirstOrDefault(item => item.Id == value.FieldId);
            if (field is not null)
                value.ValidationError = IndexFormat.Validate(value.Value, field.Format, profile.Locale);
        }
    }

    private static ScriptExecutionContext BuildExecutionContext(IndexingProfile profile, List<IndexValue> results, DefaultValueContext? context) => new()
    {
        ProfileName = profile.Name,
        DocumentNumber = context?.DocumentNumber ?? 1,
        BatchNumber = context?.BatchNumber ?? 1,
        Timestamp = context?.Timestamp ?? DateTimeOffset.Now,
        Values = results
    };

    private static void ApplyDefaults(
        IndexingProfile profile,
        List<IndexValue> results,
        DefaultValueContext? context,
        IReadOnlyList<IndexValue>? existingValues)
    {
        // A profile re-application re-extracts generated fields, but Text/Lookup/Script fields are
        // entered, chosen, or computed for the indexer to review. Preserve manual edits whether or not
        // the field also has a computed default — otherwise a manual override (including one overriding
        // a script's own output) would get silently reset on the next reprocess. This is also the
        // structural guarantee that a profile-level script (which runs earlier, in FillFieldScriptsAsync/
        // RunProfileScriptsAsync above) can never permanently clobber a manually-entered value.
        foreach (var field in profile.Fields.Where(field => field.Kind is FieldKind.Text or FieldKind.Lookup or FieldKind.Script))
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
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, profile.Locale);
        }

        var templatedFieldIds = profile.Fields
            .Where(field =>
                (field.Kind == FieldKind.Text && !string.IsNullOrEmpty(field.DefaultValueTemplate)) ||
                (field.Kind == FieldKind.Lookup && !string.IsNullOrEmpty(field.LookupKeyTemplate)))
            .Select(field => field.Id)
            .ToHashSet();
        if (templatedFieldIds.Count == 0)
            return;

        // A default can't reference another field that itself has a computed default (no chaining, no
        // cycle detection needed) — simply never offer those fields' values for lookup.
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in results)
        {
            if (!templatedFieldIds.Contains(item.FieldId))
                fields[item.FieldName] = item.Value ?? string.Empty;
        }

        var evalContext = new DefaultValueContext
        {
            DocumentNumber = context?.DocumentNumber ?? 1,
            BatchNumber = context?.BatchNumber ?? 1,
            Timestamp = context?.Timestamp ?? DateTimeOffset.Now,
            ProfileName = profile.Name,
            Fields = fields
        };

        foreach (var field in profile.Fields)
        {
            if (!templatedFieldIds.Contains(field.Id))
                continue;

            var value = results.FirstOrDefault(item => item.FieldId == field.Id);
            if (value is null || value.IsManual)
                continue;

            if (field.Kind == FieldKind.Lookup)
            {
                ApplyLookupKeyTemplate(field, value, evalContext, profile.Locale);
                continue;
            }

            if (!DefaultValueTemplateEvaluator.TryEvaluate(
                    field.DefaultValueTemplate,
                    evalContext,
                    out var evaluated,
                    out var templateError))
            {
                value.Value = string.Empty;
                value.Confidence = 0;
                value.ValidationError = templateError;
                continue;
            }

            value.Value = evaluated;
            value.Confidence = 100;
            value.ValidationError = IndexFormat.Validate(value.Value, field.Format, profile.Locale);
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
            Level = field.Level,
            Mandatory = field.Mandatory,
            HideFromIndexing = field.HideFromIndexing,
            IsReadOnly = field.IsReadOnly,
            Sensitive = field.Sensitive,
            Kind = field.Kind,
            LookupOptions = field.LookupOptions.Select(CloneLookupOption).ToList(),
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
            var page = lattices.FirstOrDefault(item => item.PageNumber == field.Zone.PageNumber)
                ?? lattices.FirstOrDefault(item => item.PageNumber == field.PageNumber);
            if (page is null)
                return value;

            var zonal = ZonalExtractor.Extract(page, field.Zone);
            value.Value = zonal.Text;
            value.Confidence = zonal.Confidence;
            value.PageNumber = page.PageNumber;
            value.Bounds = field.Zone;
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
