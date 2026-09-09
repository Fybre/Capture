using Capture.Core.Redaction;

namespace Capture.App.ViewModels;

/// <summary>One row in the Settings "Redaction sets" list — wraps a set plus whether it's a built-in
/// (see BuiltInRedactionSets), which the Settings UI uses to hide the Edit/Delete actions since
/// built-ins never pass through IRedactionEntitySetStore.</summary>
public sealed class RedactionEntitySetRow
{
    public RedactionEntitySetRow(RedactionEntitySet set, bool isBuiltIn)
    {
        Set = set;
        IsBuiltIn = isBuiltIn;
    }

    public RedactionEntitySet Set { get; }

    public Guid Id => Set.Id;

    public string Name => Set.Name;

    public bool IsBuiltIn { get; }

    public bool IsCustom => !IsBuiltIn;

    public string EntitiesSummary => string.Join(", ", Set.Entities.Select(PresidioEntityNames.Describe));
}
