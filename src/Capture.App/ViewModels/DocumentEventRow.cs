using Capture.Core.Models;

namespace Capture.App.ViewModels;

/// <summary>One step of a document's history in the review panel.</summary>
public sealed class DocumentEventRow(DocumentEvent documentEvent)
{
    public DocumentEvent Event { get; } = documentEvent;

    public string Message => Event.Message;

    public string? Detail => Event.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Event.Detail);

    /// <summary>When and who — "14 Oct, 10:42 · Capture".</summary>
    public string Byline => $"{Event.Utc.ToLocalTime():d MMM, HH:mm} · {Event.Actor}";

    public bool IsProblem => Event.Kind is DocumentEventKind.Error or DocumentEventKind.Warning;

    /// <summary>A step this slow is marked, so where time goes stands out.</summary>
    public static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(10);

    /// <summary>" · 6.3 s", or empty for an instant step.</summary>
    public string DurationText => Event.Duration is { } duration ? $" · {DocumentEvent.FormatDuration(duration)}" : string.Empty;

    public bool IsSlow => Event.Duration >= SlowStep;

    public bool HasNormalDuration => Event.Duration is not null && !IsSlow;

    public string Glyph => Event.Kind switch
    {
        DocumentEventKind.Import => "↓",
        DocumentEventKind.Decision => "✂",
        DocumentEventKind.Processing => "⚙",
        DocumentEventKind.Review => "●",
        DocumentEventKind.Edit => "✎",
        DocumentEventKind.Pages => "▤",
        DocumentEventKind.Redaction => "▮",
        DocumentEventKind.Script => "ƒ",
        DocumentEventKind.Export => "↑",
        DocumentEventKind.Lifecycle => "↺",
        DocumentEventKind.Warning => "⚠",
        DocumentEventKind.Error => "✗",
        _ => "•"
    };
}
