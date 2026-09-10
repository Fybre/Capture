using System.Text.RegularExpressions;
using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.CaptureProfiles;

/// <summary>Pure, deterministic state machine over reusable page analysis. It performs no persistence or OCR.</summary>
public sealed class CapturePlanner
{
    /// <summary>Best-effort document-type match for an already-captured document being reclassified
    /// under a different profile (see "Apply profile to selected"). Unlike <see cref="Plan"/>, there's
    /// no barcode data left to match against after initial capture — only each page's OCR/PDF text
    /// survives, via its stored <see cref="PageLattice"/> — so this only exercises regex/OCR-zone
    /// recognition rules. Falls back to the profile's default document type, then its only type if it
    /// has just one; returns null (document stays untyped, same as an Unsorted document) otherwise.</summary>
    public static DocumentTypeDefinition? MatchDocumentType(CaptureProfile profile, IReadOnlyList<PageLattice> pages)
    {
        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            var text = LatticeText.Build(page.Words).Text;
            var candidates = MatchDocumentTypes(profile.DocumentTypes, new AnalyzedPage("reclassify", page.PageNumber, text, []));
            if (candidates.Count > 0)
                return candidates[0].Type;
        }

        return profile.DocumentTypes.FirstOrDefault(type => type.Id == profile.DefaultDocumentTypeId)
            ?? (profile.DocumentTypes.Count == 1 ? profile.DocumentTypes[0] : null);
    }

    public CapturePlan Plan(CaptureProfile profile, IEnumerable<AnalyzedInput> inputs)
    {
        var batches = new List<BatchBuilder>();
        var diagnostics = new List<ProcessingDiagnostic>();
        var consumed = new List<SourcePage>();
        BatchBuilder? batch = null;
        DocumentBuilder? document = null;

        foreach (var input in inputs)
        {
            foreach (var page in input.Pages)
            {
                var pageRef = new SourcePage(page.InputId, page.PageNumber);
                var batchMatches = Evaluate(profile.Batch.StartRules, page);
                var batchStarts = IsMatch(profile.Batch.StartRules, batchMatches);
                if (batchStarts)
                {
                    document = null;
                    batch = new BatchBuilder(false, CaptureValues(profile.Batch, batchMatches), batchMatches, pageRef);
                    batches.Add(batch);
                }

                var candidates = MatchDocumentTypes(profile.DocumentTypes, page);
                if (candidates.Count > 1)
                    diagnostics.Add(new ProcessingDiagnostic("ambiguous-document-type", $"Page matches: {string.Join(", ", candidates.Select(item => item.Type.Name))}", pageRef));

                var selected = candidates.FirstOrDefault();
                var documentStarts = selected is not null &&
                    ((selected.StartMatches.Count > 0 && IsMatch(selected.Type.StartRules, selected.StartMatches))
                     || (selected.Type.IdentificationStartsNewDocument
                         && selected.RecognitionMatches.Count > 0
                         && IsMatch(selected.Type.RecognitionRules, selected.RecognitionMatches)));

                if (documentStarts || (document is null && selected is not null))
                {
                    document = new DocumentBuilder(selected!.Type, selected.AllMatches);
                    (batch ??= AddGenericBatch(batches)).Documents.Add(document);
                }

                var consume = (batchStarts && profile.Batch.TriggerPageDisposition == PageDisposition.Consume)
                    || (documentStarts && selected!.Type.TriggerPageDisposition == PageDisposition.Consume);
                if (consume)
                {
                    consumed.Add(pageRef);
                    continue;
                }

                if (batch is null) batch = AddGenericBatch(batches);
                if (document is null)
                {
                    var fallback = profile.DocumentTypes.FirstOrDefault(item => item.Id == profile.DefaultDocumentTypeId);
                    document = new DocumentBuilder(fallback, []);
                    batch.Documents.Add(document);
                    if (fallback is null)
                        diagnostics.Add(new ProcessingDiagnostic("unclassified-page", "No document type or fallback matched this page.", pageRef));
                }
                document.Pages.Add(pageRef);
            }

            if (profile.FileIsDocumentBoundary) document = null;
            if (profile.Batch.StartNewBatchForEachFile) batch = null;
        }

        return new CapturePlan(
            batches.Select(item => item.Build()).ToList(),
            diagnostics,
            consumed);
    }

    private static BatchBuilder AddGenericBatch(List<BatchBuilder> batches)
    {
        var result = new BatchBuilder(true, [], [], null);
        batches.Add(result);
        return result;
    }

    private static List<IndexValue> CaptureValues(BatchDefinition definition, IReadOnlyList<RuleMatch> matches)
    {
        var byId = matches.ToDictionary(item => item.RuleId);
        return definition.Fields.Select(field => new IndexValue
        {
            FieldId = field.Id,
            FieldName = field.Name,
            Value = field.BoundaryRuleId is { } id && byId.TryGetValue(id, out var match) ? match.CapturedValue ?? string.Empty : string.Empty,
            Confidence = field.BoundaryRuleId is { } confidenceRuleId && byId.TryGetValue(confidenceRuleId, out var confidenceMatch)
                ? (float)(confidenceMatch.Confidence ?? 100)
                : 0
        }).ToList();
    }

    private static List<TypeMatch> MatchDocumentTypes(IEnumerable<DocumentTypeDefinition> types, AnalyzedPage page)
    {
        var results = new List<TypeMatch>();
        foreach (var type in types)
        {
            var starts = Evaluate(type.StartRules, page);
            var recognition = Evaluate(type.RecognitionRules, page);
            if (IsMatch(type.StartRules, starts) || IsMatch(type.RecognitionRules, recognition))
                results.Add(new TypeMatch(type, starts, recognition));
        }
        return results;
    }

    private static bool IsMatch(RuleSet rules, IReadOnlyList<RuleMatch> matches)
    {
        if (rules.MatchMode == SeparationMatchMode.None) return false;
        if (rules.Rules.Count == 0) return false;
        return rules.MatchMode switch
        {
            SeparationMatchMode.All => matches.Count == rules.Rules.Count,
            SeparationMatchMode.AtLeast => matches.Count >= Math.Max(1, rules.MatchMinimum),
            _ => matches.Count > 0
        };
    }

    private static List<RuleMatch> Evaluate(RuleSet set, AnalyzedPage page)
    {
        var matches = new List<RuleMatch>();
        if (set.MatchMode == SeparationMatchMode.None) return matches;
        foreach (var rule in set.Rules)
        {
            RuleMatch? match = rule.Type switch
            {
                SeparationStrategyType.Regex => RegexMatch(rule, page.Text),
                SeparationStrategyType.OcrZone => ZoneTextMatch(rule, page.RuleTexts is not null && page.RuleTexts.TryGetValue(rule.Id, out var zoneText) ? zoneText : page.Text),
                SeparationStrategyType.Barcode => BarcodeMatch(rule, page.Barcodes),
                SeparationStrategyType.BlankPage when page.PreMatchedRuleIds?.Contains(rule.Id) == true || (page.PreMatchedRuleIds is null && page.IsBlank) => new RuleMatch(rule.Id, null, 100),
                SeparationStrategyType.EveryNPages when rule.PageCount > 0 && page.PageNumber % rule.PageCount == 0 => new RuleMatch(rule.Id, null, 100),
                _ => null
            };
            if (match is not null) matches.Add(match);
        }
        return matches;
    }

    private static RuleMatch? RegexMatch(SeparationStrategy rule, string text)
    {
        if (string.IsNullOrWhiteSpace(rule.TextPattern)) return null;
        try
        {
            var match = Regex.Match(text ?? string.Empty, rule.TextPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            return match.Success ? new RuleMatch(rule.Id, Captured(match, fallbackToWholeMatch: false), 100) : null;
        }
        catch (ArgumentException) { return null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static RuleMatch? ZoneTextMatch(SeparationStrategy rule, string text)
    {
        if (string.IsNullOrWhiteSpace(rule.TextPattern))
            return string.IsNullOrWhiteSpace(text) ? null : new RuleMatch(rule.Id, text.Trim(), 100);
        return RegexMatch(rule, text);
    }

    private static RuleMatch? BarcodeMatch(SeparationStrategy rule, IReadOnlyList<AnalyzedBarcode> barcodes)
    {
        foreach (var barcode in barcodes)
        {
            if (barcode.RuleId is { } ruleId && ruleId != rule.Id) continue;
            if (!string.IsNullOrWhiteSpace(rule.BarcodeFormat) && !string.Equals(rule.BarcodeFormat, barcode.Format, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(rule.BarcodeValuePattern)) return new RuleMatch(rule.Id, barcode.Value, barcode.Confidence ?? 100);
            try
            {
                var match = Regex.Match(barcode.Value, rule.BarcodeValuePattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                if (match.Success) return new RuleMatch(rule.Id, Captured(match, fallbackToWholeMatch: true), barcode.Confidence ?? 100);
            }
            catch (ArgumentException) { }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }

    private static string? Captured(Match match, bool fallbackToWholeMatch)
    {
        for (var index = 1; index < match.Groups.Count; index++)
            if (match.Groups[index].Success) return match.Groups[index].Value;
        return fallbackToWholeMatch ? match.Value : null;
    }

    private sealed record TypeMatch(DocumentTypeDefinition Type, List<RuleMatch> StartMatches, List<RuleMatch> RecognitionMatches)
    {
        public IReadOnlyList<RuleMatch> AllMatches => StartMatches.Concat(RecognitionMatches).DistinctBy(item => item.RuleId).ToList();
    }

    private sealed class BatchBuilder(
        bool generic,
        List<IndexValue> values,
        IReadOnlyList<RuleMatch> matches,
        SourcePage? boundaryPage)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<DocumentBuilder> Documents { get; } = [];
        public PlannedBatch Build() => new(
            Id,
            generic,
            values,
            Documents.Where(item => item.Pages.Count > 0).Select(item => item.Build()).ToList(),
            matches,
            boundaryPage);
    }

    private sealed class DocumentBuilder(DocumentTypeDefinition? type, IReadOnlyList<RuleMatch> matches)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<SourcePage> Pages { get; } = [];
        public PlannedDocument Build() => new(Id, type, Pages, matches);
    }
}
