namespace Capture.Core.Redaction;

/// <summary>One PII match. Start/End are .NET UTF-16 char offsets into the analyzed text — a detector
/// implementation whose underlying engine reports offsets differently (e.g. Presidio's Python Unicode
/// code-point offsets) is responsible for converting before returning matches here, so callers never
/// need to know anything about the detector's own wire format.</summary>
public sealed record PiiMatch(string EntityType, int Start, int End, float Score);

public interface IPiiDetector
{
    /// <summary>True once the detector is actually usable (e.g. its bundled executable is present on
    /// disk and was reachable) — checked before every analysis attempt so callers can skip Presidio-
    /// backed detection cleanly rather than fail.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<PiiMatch>> AnalyzeAsync(
        string text,
        IReadOnlyList<string> entities,
        string language,
        CancellationToken cancellationToken = default);
}
