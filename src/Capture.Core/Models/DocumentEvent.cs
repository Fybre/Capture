namespace Capture.Core.Models;

/// <summary>What kind of step a <see cref="DocumentEvent"/> records — used to group and mark the
/// document's history.</summary>
public enum DocumentEventKind
{
    Import,
    Decision,
    Processing,
    Review,
    Edit,
    Pages,
    Redaction,
    Script,
    Export,
    Lifecycle,
    Warning,
    Error
}

/// <summary>One step in a document's history: what happened, when, and who did it — Capture itself or
/// the person signed in to the computer. A document's events are its audit trail, from import to export,
/// and are removed with it when it's permanently deleted.</summary>
/// <param name="Message">One line a person can read, e.g. "Marked ready".</param>
/// <param name="Detail">More, when there is more to say — the reasons, or the values before and after.
/// Never holds a sensitive field's value.</param>
/// <param name="Duration">How long the step took, for steps that do real work (reading text, AI, scripts,
/// redaction, exports, rewriting pages) — to show where time goes. Null for instant steps.</param>
public sealed record DocumentEvent(
    Guid DocumentId,
    DateTimeOffset Utc,
    DocumentEventKind Kind,
    string Message,
    string Actor,
    string? Detail = null,
    TimeSpan? Duration = null)
{
    public const string CaptureActor = "Capture";

    /// <summary>The computer's signed-in user, who did anything done by hand.</summary>
    public static string CurrentUser
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(Environment.UserName) ? "User" : Environment.UserName;
            }
            catch (InvalidOperationException)
            {
                return "User";
            }
        }
    }

    public static DocumentEvent ByCapture(Guid documentId, DocumentEventKind kind, string message, string? detail = null, TimeSpan? duration = null) =>
        new(documentId, DateTimeOffset.UtcNow, kind, message, CaptureActor, detail, duration);

    public static DocumentEvent ByUser(Guid documentId, DocumentEventKind kind, string message, string? detail = null, TimeSpan? duration = null) =>
        new(documentId, DateTimeOffset.UtcNow, kind, message, CurrentUser, detail, duration);

    /// <summary>"320 ms", "6.3 s", "2 min 5 s".</summary>
    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 1 ? $"{Math.Max(0, (int)Math.Round(duration.TotalMilliseconds))} ms"
        : duration.TotalSeconds < 60 ? $"{duration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s"
        : $"{(int)duration.TotalMinutes} min {duration.Seconds} s";
}
