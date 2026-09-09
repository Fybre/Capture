using Capture.Core.Models;

namespace Capture.Core.CaptureProfiles;

public enum BoundaryScope { Batch, DocumentRecognition, DocumentStart }

public sealed record RuleMatch(Guid RuleId, string? CapturedValue, double? Confidence);

public sealed record BoundaryDecision(
    BoundaryScope Scope,
    Guid? DocumentTypeId,
    IReadOnlyList<RuleMatch> Matches,
    PageDisposition PageDisposition);

public sealed record SourcePage(string InputId, int PageNumber);

public sealed record CapturePlan(
    IReadOnlyList<PlannedBatch> Batches,
    IReadOnlyList<ProcessingDiagnostic> Diagnostics,
    IReadOnlyList<SourcePage> ConsumedPages);

/// <summary>A non-persisted, production-equivalent capture result used by the profile designer.</summary>
public sealed record CapturePreviewResult(
    CapturePlan Plan,
    IReadOnlyList<CapturePreviewBatch> Batches);

public sealed record CapturePreviewBatch(
    int Number,
    IReadOnlyList<IndexValue> IndexValues,
    IReadOnlyList<CapturePreviewDocument> Documents);

public sealed record CapturePreviewDocument(
    int Number,
    string DocumentType,
    string SourceFiles,
    int PageCount,
    DocumentStatus Status,
    IReadOnlyList<IndexValue> IndexValues);

public sealed record PlannedBatch(
    Guid PlanId,
    bool IsGeneric,
    IReadOnlyList<IndexValue> CapturedValues,
    IReadOnlyList<PlannedDocument> Documents,
    IReadOnlyList<RuleMatch>? BoundaryMatches = null,
    SourcePage? BoundaryPage = null);

public sealed record PlannedDocument(
    Guid PlanId,
    DocumentTypeDefinition? Type,
    IReadOnlyList<SourcePage> SourcePages,
    IReadOnlyList<RuleMatch> BoundaryMatches);

public sealed record ProcessingDiagnostic(string Code, string Message, SourcePage? Page = null);

public sealed record AnalyzedPage(
    string InputId,
    int PageNumber,
    string Text,
    IReadOnlyList<AnalyzedBarcode> Barcodes,
    bool IsBlank = false,
    IReadOnlyDictionary<Guid, string>? RuleTexts = null,
    IReadOnlySet<Guid>? PreMatchedRuleIds = null);

public sealed record AnalyzedBarcode(string Value, string? Format = null, double? Confidence = null, Guid? RuleId = null);

public sealed record AnalyzedInput(string Id, IReadOnlyList<AnalyzedPage> Pages);
