namespace Capture.Core.Profiles;

/// <summary>Names every piece of executable C# a profile carries — profile-level <see cref="FieldScript"/>s,
/// Script-kind field expressions, and Button-kind field scripts alike. Used by the profile-import UI to
/// warn before bringing in a profile that will run arbitrary code once scripting is enabled (see
/// ProfilesViewModel.ImportProfileAsync). Keep this in sync with every place a profile can carry a
/// script — a script-carrying <see cref="FieldKind"/> added here without a matching check is a silent
/// bypass of that warning.</summary>
public static class ProfileScriptInventory
{
    public static IReadOnlyList<string> NamesIn(IndexingProfile profile)
    {
        var names = profile.Scripts
            .Where(script => !string.IsNullOrWhiteSpace(script.Source))
            .Select(script => script.Name)
            .ToList();

        names.AddRange(profile.Fields
            .Where(field => field.Kind == FieldKind.Script && !string.IsNullOrWhiteSpace(field.ScriptExpression))
            .Select(field => $"{field.Name} (Script field)"));

        names.AddRange(profile.Fields
            .Where(field => field.Kind == FieldKind.Button && !string.IsNullOrWhiteSpace(field.ButtonScriptSource))
            .Select(field => $"{field.Name} (Button field)"));

        return names;
    }
}
