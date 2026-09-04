using Capture.Core.Profiles;

namespace Capture.Core.Import;

/// <summary>How <see cref="ImportProfile.Strategies"/> as a whole combine into a single "does this
/// page start a new document" decision.</summary>
public enum SeparationMatchMode
{
    /// <summary>Every strategy in the list must hit this page.</summary>
    All = 0,

    /// <summary>At least one strategy must hit this page.</summary>
    Any = 1,

    /// <summary>At least <see cref="ImportProfile.MatchMinimum"/> strategies must hit this page.</summary>
    AtLeast = 2
}

public enum SeparationStrategyType
{
    Barcode = 0,
    BlankPage = 1,
    EveryNPages = 2,
    Regex = 3,
    OcrZone = 4,

    /// <summary>Compares a page's embedding against <see cref="SeparationStrategy.ReferenceEmbedding"/>.
    /// Modeled now, but <see cref="PageSeparator"/>'s evaluator is a stub that never hits until the
    /// embedding backend (model download + inference + comparison) ships as its own follow-up phase.</summary>
    Similarity = 5
}

/// <summary>
/// One separation condition inside <see cref="ImportProfile.Strategies"/>. Like <c>IndexField</c>
/// covers several field kinds with flat, kind-specific optional fields on one class rather than a type
/// hierarchy, this covers several strategy kinds the same way — see the per-property comments below for
/// which <see cref="Type"/> each applies to.
/// </summary>
public sealed class SeparationStrategy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SeparationStrategyType Type { get; set; }

    /// <summary>Optional label shown on this strategy's card in the Designer — purely cosmetic.</summary>
    public string? Name { get; set; }

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.EveryNPages"/>. This strategy
    /// hits every Nth page, counted independently of every other strategy in the list (including
    /// another <see cref="SeparationStrategyType.EveryNPages"/> strategy, if the profile somehow has
    /// more than one).</summary>
    public int PageCount { get; set; } = 1;

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.BlankPage"/>.</summary>
    public int BlankInkPercent { get; set; }

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.Barcode"/>,
    /// <see cref="SeparationStrategyType.OcrZone"/>, and (once the embedding backend ships)
    /// <see cref="SeparationStrategyType.Similarity"/> — drawn against the profile's shared
    /// <see cref="ImportProfile.SampleFileName"/>, not a sample of this strategy's own.</summary>
    public ZoneRect? Zone { get; set; }

    /// <summary>Only meaningful alongside <see cref="Zone"/> — which page of the shared sample it was
    /// drawn on.</summary>
    public int ZonePageNumber { get; set; } = 1;

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.Barcode"/> — an optional
    /// symbology filter (e.g. "CODE_128"); null/empty matches any format the decoder returns.</summary>
    public string? BarcodeFormat { get; set; }

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.Barcode"/> — an optional regex
    /// the decoded barcode text must match; null/empty matches any value.</summary>
    public string? BarcodeValuePattern { get; set; }

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.Regex"/> (matched against the
    /// whole page's OCR'd text — a null/empty pattern never hits, since there's no other signal to
    /// fall back on) and <see cref="SeparationStrategyType.OcrZone"/> (matched against just the
    /// drawn zone's OCR'd text — a null/empty pattern hits on any non-empty zone text, mirroring how
    /// an empty <see cref="BarcodeValuePattern"/> already means "matches any value").</summary>
    public string? TextPattern { get; set; }

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.Similarity"/> — the reference
    /// embedding to compare each page against. Stays null until the embedding backend ships; the
    /// strategy never hits before then regardless of this value.</summary>
    public float[]? ReferenceEmbedding { get; set; }

    /// <summary>Only meaningful for <see cref="SeparationStrategyType.Similarity"/> — cosine-similarity
    /// threshold a page's embedding must meet or exceed against <see cref="ReferenceEmbedding"/> to
    /// count as a hit.</summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>When true and this strategy contributes to a page being a split boundary, that page is
    /// dropped from both resulting documents. If several strategies hit the same boundary page and
    /// disagree, the page is discarded if *any* of them say so — simple, predictable, and this
    /// codebase's existing convention for the same field on <c>BatchProfile</c>/the old
    /// single-trigger <c>ImportProfile</c>.</summary>
    public bool DiscardSeparatorPage { get; set; }
}
