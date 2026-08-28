using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public sealed class ProfileApplicator : IProfileApplicator
{
    private readonly IBarcodeDecoder? _barcodes;
    private readonly IAiExtractor? _ai;

    public ProfileApplicator(IBarcodeDecoder? barcodes = null, IAiExtractor? ai = null)
    {
        _barcodes = barcodes;
        _ai = ai;
    }

    public IReadOnlyList<IndexValue> Apply(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        MacroContext? macro = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null)
    {
        var results = ExtractNonMacros(profile, lattices, pages, batchSeparatorValue);
        AppendMacros(profile, results, macro);
        return results;
    }

    public async Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        MacroContext? macro = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        CancellationToken cancellationToken = default)
    {
        var results = ExtractNonMacros(profile, lattices, pages, batchSeparatorValue);
        await FillAiAsync(profile, lattices, results, cancellationToken).ConfigureAwait(false);
        AppendMacros(profile, results, macro);
        return results;
    }

    private List<IndexValue> ExtractNonMacros(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        IReadOnlyList<DocumentPage>? pages,
        string? batchSeparatorValue)
    {
        var results = new List<IndexValue>(profile.Fields.Count);
        foreach (var field in profile.Fields.Where(item => item.Kind != FieldKind.Macro))
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

    private static void AppendMacros(
        IndexingProfile profile,
        List<IndexValue> results,
        MacroContext? macro)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in results)
            fields[item.FieldName] = item.Value ?? string.Empty;
        var context = new MacroContext
        {
            DocumentNumber = macro?.DocumentNumber ?? 1,
            BatchNumber = macro?.BatchNumber ?? 1,
            Timestamp = macro?.Timestamp ?? DateTimeOffset.Now,
            ProfileName = profile.Name,
            Fields = fields
        };

        foreach (var field in profile.Fields.Where(item => item.Kind == FieldKind.Macro))
        {
            var value = MacroEvaluator.Evaluate(field.Macro ?? [], context);
            fields[field.Name] = value;
            var extracted = new IndexValue
            {
                FieldId = field.Id,
                FieldName = field.Name,
                Format = field.Format,
                Level = field.Level,
                Mandatory = field.Mandatory,
                HideFromIndexing = field.HideFromIndexing,
                Value = value,
                Confidence = 100
            };
            extracted.ValidationError = IndexFormat.Validate(extracted.Value, field.Format, profile.Locale);
            results.Add(extracted);
            context = new MacroContext
            {
                DocumentNumber = context.DocumentNumber,
                BatchNumber = context.BatchNumber,
                Timestamp = context.Timestamp,
                ProfileName = context.ProfileName,
                Fields = fields
            };
        }
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
