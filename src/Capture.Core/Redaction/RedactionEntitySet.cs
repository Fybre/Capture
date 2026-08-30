namespace Capture.Core.Redaction;

/// <summary>A named, reusable collection of Presidio entity type codes — what a profile's Redaction
/// settings or the Inbox's manual "Redact" picker actually offer is a choice of these sets, not a raw
/// checklist of every entity type. Built-in sets (see <see cref="BuiltInRedactionSets"/>) are not
/// persisted through <see cref="IRedactionEntitySetStore"/> — only user-created custom sets are.</summary>
public sealed class RedactionEntitySet
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public List<string> Entities { get; set; } = [];

    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
}
