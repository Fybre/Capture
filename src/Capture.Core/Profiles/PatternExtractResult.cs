namespace Capture.Core.Profiles;

public sealed record PatternExtractResult(string Text, float Confidence, ZoneRect? Bounds, int PageNumber);
