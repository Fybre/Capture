using Capture.Core.Profiles;

namespace Capture.Tests;

/// <summary>Covers the profile-import "this profile contains executable code" warning's detection
/// logic (ProfilesViewModel.ImportProfileAsync uses this directly) — every FieldKind that carries a
/// script must be found here, since a gap is a silent bypass of that warning.</summary>
public class ProfileScriptInventoryTests
{
    [Fact]
    public void Empty_profile_has_no_scripts()
    {
        var profile = new IndexingProfile { Fields = [new IndexField { Name = "Total", Kind = FieldKind.Text }] };

        Assert.Empty(ProfileScriptInventory.NamesIn(profile));
    }

    [Fact]
    public void Finds_profile_level_scripts_by_name()
    {
        var profile = new IndexingProfile
        {
            Scripts = [new FieldScript { Name = "Enrich", Source = "Fields[\"Total\"].Value = \"42\";" }]
        };

        Assert.Equal(["Enrich"], ProfileScriptInventory.NamesIn(profile));
    }

    [Fact]
    public void Finds_script_kind_fields_by_name()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Computed", Kind = FieldKind.Script, ScriptExpression = "Fields[\"Other\"].Value" }]
        };

        var names = ProfileScriptInventory.NamesIn(profile);

        Assert.Contains("Computed", names.Single());
    }

    [Fact]
    public void Finds_button_kind_fields_by_name()
    {
        // The exact bug reported: a profile with only a Button field's script previously produced an
        // empty list here, letting ImportProfileAsync skip the warning entirely.
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField
                {
                    Name = "Lookup customer",
                    Kind = FieldKind.Button,
                    ButtonScriptSource = "Fields[\"Customer Name\"].Value = \"ACME\";"
                }
            ]
        };

        var names = ProfileScriptInventory.NamesIn(profile);

        var name = Assert.Single(names);
        Assert.Contains("Lookup customer", name);
    }

    [Fact]
    public void A_button_field_with_no_script_configured_yet_is_not_flagged()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Lookup customer", Kind = FieldKind.Button, ButtonScriptSource = "" }]
        };

        Assert.Empty(ProfileScriptInventory.NamesIn(profile));
    }

    [Fact]
    public void Reports_every_kind_of_script_a_profile_carries_together()
    {
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField { Name = "Computed", Kind = FieldKind.Script, ScriptExpression = "..." },
                new IndexField { Name = "Lookup", Kind = FieldKind.Button, ButtonScriptSource = "..." }
            ],
            Scripts = [new FieldScript { Name = "Enrich", Source = "..." }]
        };

        var names = ProfileScriptInventory.NamesIn(profile);

        Assert.Equal(3, names.Count);
    }
}
