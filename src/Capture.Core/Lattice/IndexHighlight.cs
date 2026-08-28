namespace Capture.Core.Lattice;

public sealed class IndexHighlight
{
    public Guid FieldId { get; init; }
    public string FieldName { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public bool IsSelected { get; init; }
    public bool CanEdit { get; init; } = true;
    public bool IsSearchZone { get; init; }
}
