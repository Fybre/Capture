namespace Capture.Core.Redaction;

public enum RedactionSource
{
    Presidio = 0,
    SensitiveField = 1,
    Manual = 2
}

public enum RedactionDecision
{
    Pending = 0,
    Confirmed = 1,
    Rejected = 2
}

/// <summary>One suggested redaction box on a document, produced by <c>RedactionDetectionStep</c> and
/// reviewed/applied via <c>RedactionApplier</c>. Coordinates are normalized (0-1), matching
/// <c>ZoneRect</c>/<c>IndexHighlight</c> conventions elsewhere in the app.</summary>
public sealed class RedactionCandidate
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RedactionSource Source { get; init; }

    /// <summary>Presidio entity type (e.g. "PERSON") or the source field's Name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Matched text snippet, shown in the review list.</summary>
    public string? PreviewText { get; init; }

    public int PageNumber { get; init; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    /// <summary>0-1. Presidio's confidence for NLP matches; hardcoded 1.0 for Sensitive-field
    /// candidates, since a user explicitly marking a field Sensitive isn't a probabilistic guess.</summary>
    public float Score { get; init; }

    /// <summary>Defaults to Confirmed — suggestions are pre-accepted (opt-out review) so a reviewer who
    /// does nothing still gets full redaction; they actively reject false positives instead of having to
    /// individually accept every true one.</summary>
    public RedactionDecision Decision { get; set; } = RedactionDecision.Confirmed;
}
