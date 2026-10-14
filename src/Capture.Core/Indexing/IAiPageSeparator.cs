namespace Capture.Core.Indexing;

/// <summary>One document type the AI can choose from: <see cref="Id"/> is what the model answers with.</summary>
public sealed record AiSeparationType(string Id, string Name, string Description);

/// <summary>One page in the order it was captured. <see cref="ForceNew"/> marks a page that starts a
/// document whatever the AI says (the first page of a file that is its own document), so it is only
/// classified.</summary>
public sealed record AiSeparationPage(string Text, string? ImagePath = null, bool IsBlank = false, bool ForceNew = false);

/// <summary>The AI's answer for one page: whether it starts a new document, which type that is (an
/// <see cref="AiSeparationType.Id"/>, or null when none fits), how sure it is (0–100) and why. When no
/// type fits, <see cref="SuggestedType"/> is the AI's own short label for the document, if it gave one —
/// a hint at a type the profile is missing.</summary>
public sealed record AiPageVerdict(bool StartsNewDocument, string? TypeId, int Confidence, string Reason, string? SuggestedType = null);

/// <summary>Decides where documents start and what type each is, one page at a time with its neighbours
/// for context.</summary>
public interface IAiPageSeparator
{
    bool IsConfigured { get; }

    /// <summary>One verdict per page, in the same order. Never throws for a single page's failure: that
    /// page gets confidence 0 and an "AI failed" reason, and continues the previous document. With
    /// <paramref name="types"/> null the AI only splits: it's shown no types, its verdicts carry none, and
    /// pages that must start a document aren't sent at all. <paramref name="guidance"/> is the profile's
    /// own extra rules for the AI, if any. <paramref name="progress"/> gets the number of pages done so
    /// far, after each one.</summary>
    Task<IReadOnlyList<AiPageVerdict>> AnalyzeAsync(
        IReadOnlyList<AiSeparationPage> pages,
        IReadOnlyList<AiSeparationType>? types,
        string? guidance = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
