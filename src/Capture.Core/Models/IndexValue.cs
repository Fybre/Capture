using Capture.Core.Profiles;

namespace Capture.Core.Models;

public sealed class IndexValue
{
    public Guid FieldId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public FieldFormat Format { get; set; } = FieldFormat.String;
    public IndexLevel Level { get; set; } = IndexLevel.Document;
    public bool Mandatory { get; set; }
    public string Value { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public bool IsManual { get; set; }
    public int PageNumber { get; set; }
    public ZoneRect? Bounds { get; set; }
    public string? ValidationError { get; set; }
    public bool HideFromIndexing { get; set; }

    public bool IsMissing => Mandatory && string.IsNullOrWhiteSpace(Value);

    public bool IsLowConfidence(int threshold) =>
        !IsManual && !IsMissing && !string.IsNullOrWhiteSpace(Value) && Confidence < threshold;
}
