using System.Text.RegularExpressions;
using Capture.Core.Import;
using Capture.Core.Indexing;
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

    /// <summary>The profile's document types as the AI sees them for separation. A fallback type with no
    /// AI description is the catch-all, not a kind of document: the AI answers "other" for what fits
    /// nothing, which lands there anyway — offering it by name alone made the AI pick it arbitrarily.
    /// Null when the AI only splits documents (<see cref="CaptureProfile.AiClassifies"/> off): it isn't
    /// shown any types.</summary>
    public static IReadOnlyList<AiSeparationType>? AiTypes(CaptureProfile profile) =>
        !profile.AiClassifies ? null : profile.DocumentTypes
            .Where(type => type.Id != profile.DefaultDocumentTypeId || !string.IsNullOrWhiteSpace(type.AiDescription))
            .Select(type => new AiSeparationType(AiTypeId(type), type.Name.Trim(), type.AiDescription?.Trim() ?? string.Empty))
            .ToList();

    /// <summary>A short id the AI answers with: the type's name in lower case with dashes. Names are
    /// unique within a profile, so these are too.</summary>
    public static string AiTypeId(DocumentTypeDefinition type)
    {
        var slug = Regex.Replace(type.Name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug : type.Id.ToString("N")[..8];
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

                // A type set to let the AI decide drops out of rule matching on pages the AI analysed.
                var ai = profile.AiSeparation ? page.Ai : null;
                var candidates = MatchDocumentTypes(
                    ai is null || !profile.AiClassifies ? profile.DocumentTypes : profile.DocumentTypes.Where(type => !type.AiIgnoresRules), page);
                if (candidates.Count > 1)
                    diagnostics.Add(new ProcessingDiagnostic("ambiguous-document-type", $"Page matches: {string.Join(", ", candidates.Select(item => item.Type.Name))}", pageRef));

                var selected = candidates.FirstOrDefault();
                var documentStarts = selected is not null &&
                    ((selected.StartMatches.Count > 0 && IsMatch(selected.Type.StartRules, selected.StartMatches))
                     || (selected.Type.IdentificationStartsNewDocument
                         && selected.RecognitionMatches.Count > 0
                         && IsMatch(selected.Type.RecognitionRules, selected.RecognitionMatches)));

                // AI separation only decides what no rule did: a rule-matched type and a rule-matched
                // start always win, and a separator sheet is consumed below as usual.
                if (ai is not null && !documentStarts && !profile.AiClassifies)
                {
                    // Split only: the AI decides where documents start; each document's type comes
                    // from its pages' rules once it's complete (see ResolveTypeFromPages).
                    if (ai.StartsNewDocument || document is null)
                    {
                        document = new DocumentBuilder(null, []) { TypeFromPages = true };
                        (batch ??= AddGenericBatch(batches)).Documents.Add(document);
                    }

                    if (ai.Confidence < profile.AiSeparationMinConfidence)
                        document.ReviewReasons.Add(AiReviewReason(ai, page, ai.StartsNewDocument, null));
                }
                else if (ai is not null && !documentStarts)
                {
                    var aiType = ai.TypeId is null ? null : profile.DocumentTypes.FirstOrDefault(type => AiTypeId(type) == ai.TypeId);
                    var type = selected?.Type ?? aiType ?? profile.DocumentTypes.FirstOrDefault(item => item.Id == profile.DefaultDocumentTypeId);
                    var aiStarts = ai.StartsNewDocument && (aiType?.AiStartsNewDocument ?? true);
                    if (aiStarts || document is null)
                    {
                        document = new DocumentBuilder(type, selected?.AllMatches ?? RuleValueMatches(type, page))
                        {
                            AiSuggestedType = selected is null && aiType is null ? ai.SuggestedType : null
                        };
                        (batch ??= AddGenericBatch(batches)).Documents.Add(document);
                        if (type is null)
                            diagnostics.Add(new ProcessingDiagnostic("unclassified-page", "The AI matched no document type and there's no fallback type.", pageRef));
                        // A confident "none of these" still needs a person to choose the type; an unsure
                        // answer is flagged below instead.
                        if (selected is null && aiType is null && ai.Confidence >= profile.AiSeparationMinConfidence)
                            document.ReviewReasons.Add(NoTypeFitsReason(ai, type));
                    }

                    if (ai.Confidence < profile.AiSeparationMinConfidence)
                        document.ReviewReasons.Add(AiReviewReason(ai, page, aiStarts, aiType ?? type));
                }
                else if (documentStarts || (document is null && selected is not null))
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
                if (profile.AiSeparation && page.Ai is null && !document.ReviewReasons.Contains(AiNotSetUpReason))
                    document.ReviewReasons.Add(AiNotSetUpReason);
                document.Pages.Add(pageRef);
                document.Analyzed.Add(page);
            }

            if (profile.FileIsDocumentBoundary) document = null;
            if (profile.Batch.StartNewBatchForEachFile) batch = null;
        }

        foreach (var built in batches.SelectMany(item => item.Documents).Where(item => item.TypeFromPages))
            ResolveTypeFromPages(profile, built);

        return new CapturePlan(
            batches.Select(item => item.Build()).ToList(),
            diagnostics,
            consumed);
    }

    public const string AiNotSetUpReason = "AI separation is on, but AI isn't set up — documents were split by rules only. Choose a provider in Settings ▸ AI extraction.";

    /// <summary>The type of a document the AI split off: the first of its pages, in order, that a type's
    /// identify or start rules match — the AI's first page often isn't the one a rule recognises — or
    /// the fallback type. Pages matching different types suggest the AI joined two documents.</summary>
    private static void ResolveTypeFromPages(CaptureProfile profile, DocumentBuilder document)
    {
        TypeMatch? first = null;
        var others = new List<(DocumentTypeDefinition Type, int Page)>();
        foreach (var page in document.Analyzed)
        {
            foreach (var match in MatchDocumentTypes(profile.DocumentTypes, page))
            {
                if (first is null)
                    first = match;
                else if (match.Type.Id != first.Type.Id && others.All(item => item.Type.Id != match.Type.Id))
                    others.Add((match.Type, page.PageNumber));
            }
        }

        document.Type = first?.Type ?? profile.DocumentTypes.FirstOrDefault(item => item.Id == profile.DefaultDocumentTypeId);
        document.Matches = first?.AllMatches ?? [];
        if (first is not null && others.Count > 0)
        {
            var firstPage = document.Analyzed.First(page => MatchDocumentTypes([first.Type], page).Count > 0).PageNumber;
            var listed = string.Join(", ", others.Select(item => $"{item.Type.Name} on page {item.Page}"));
            document.ReviewReasons.Add(
                $"Pages match more than one document type ({first.Type.Name} on page {firstPage}, {listed}) — the AI may have joined two documents. It was given {first.Type.Name}.");
        }
    }

    private static string NoTypeFitsReason(AiPageVerdict ai, DocumentTypeDefinition? fallback)
    {
        var label = ai.SuggestedType is { } suggested ? $" — the AI calls it \"{suggested}\"" : string.Empty;
        var kept = fallback is null ? "it has no type" : $"it was given the fallback type, {fallback.Name}";
        return $"None of this profile's document types fit{label}, so {kept}. Check the document type.";
    }

    private static string AiReviewReason(AiPageVerdict ai, AnalyzedPage page, bool starts, DocumentTypeDefinition? type)
    {
        var decision = starts
            ? $"starts a new {type?.Name ?? "document"}"
            : "continues the document before it";
        var reason = string.IsNullOrWhiteSpace(ai.Reason) ? string.Empty : $": {ai.Reason}";
        return ai.Reason.StartsWith("AI failed", StringComparison.Ordinal)
            ? $"Page {page.PageNumber}: {ai.Reason}"
            : $"AI was {ai.Confidence}% sure page {page.PageNumber} {decision}{reason}";
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

    // The rule matches of a type the AI chose while its rules were ignored, so boundary-rule fields
    // still pick up their captured values.
    private static IReadOnlyList<RuleMatch> RuleValueMatches(DocumentTypeDefinition? type, AnalyzedPage page) =>
        type is { AiIgnoresRules: true }
            ? new TypeMatch(type, Evaluate(type.StartRules, page), Evaluate(type.RecognitionRules, page)).AllMatches
            : [];

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
                SeparationStrategyType.CaptureSeparator => SeparatorSheetMatch(rule, page.Barcodes),
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

    private static RuleMatch? SeparatorSheetMatch(SeparationStrategy rule, IReadOnlyList<AnalyzedBarcode> barcodes)
    {
        var barcode = barcodes.FirstOrDefault(item =>
            (item.RuleId is null || item.RuleId == rule.Id) && CaptureSeparatorSheet.Matches(rule.SeparatorKind, item.Value));
        return barcode is null ? null : new RuleMatch(rule.Id, null, barcode.Confidence ?? 100);
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
        public DocumentTypeDefinition? Type { get; set; } = type;
        public IReadOnlyList<RuleMatch> Matches { get; set; } = matches;
        public List<SourcePage> Pages { get; } = [];
        public List<AnalyzedPage> Analyzed { get; } = [];
        public List<string> ReviewReasons { get; } = [];
        public string? AiSuggestedType { get; init; }

        /// <summary>Split off by the AI with its type still to come from its pages' rules.</summary>
        public bool TypeFromPages { get; init; }

        public PlannedDocument Build() => new(Id, Type, Pages, Matches, ReviewReasons.Count > 0 ? ReviewReasons : null, AiSuggestedType);
    }
}
