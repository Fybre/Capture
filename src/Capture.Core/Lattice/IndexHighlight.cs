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

    /// <summary>True for a redaction candidate box (as opposed to an index-field highlight) — driving
    /// distinct render styling in <c>PagePreview</c>.</summary>
    public bool IsRedaction { get; init; }

    /// <summary>Only meaningful when <see cref="IsRedaction"/> — the reviewer has excluded this
    /// candidate, rendered as an outline only, no fill, to show "considered but excluded".</summary>
    public bool IsRejected { get; init; }
}
