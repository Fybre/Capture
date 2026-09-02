using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public class JsonProfileStoreTests
{
    [Fact]
    public async Task Roundtrips_profile_with_zonal_field()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);

        var profile = new IndexingProfile
        {
            Name = "Payment slip",
            SampleFileName = "sample.pdf",
            Fields =
            [
                new IndexField
                {
                    Name = "PurchaseNo",
                    Format = FieldFormat.String,
                    Mandatory = true,
                    PageNumber = 1,
                    Zone = new ZoneRect { PageNumber = 1, X = 0.7f, Y = 0.2f, Width = 0.2f, Height = 0.05f }
                }
            ]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Payment slip", loaded!.Name);
        var field = Assert.Single(loaded.Fields);
        Assert.Equal("PurchaseNo", field.Name);
        Assert.True(field.Mandatory);
        Assert.NotNull(field.Zone);
        Assert.Equal(0.7f, field.Zone!.X, 3);

        var all = await store.GetAllAsync();
        Assert.Contains(all, item => item.Id == profile.Id);

        await store.DeleteAsync(profile.Id);
        Assert.Null(await store.GetAsync(profile.Id));
    }

    [Fact]
    public async Task Roundtrips_key_value_field()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-kv-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var profile = new IndexingProfile
        {
            Name = "KV",
            Fields =
            [
                new IndexField
                {
                    Name = "InvoiceNo",
                    Kind = FieldKind.KeyValue,
                    KeyPattern = @"Invoice\s*No",
                    ValuePattern = @"\d+",
                    Occurrence = MatchOccurrence.First,
                    PageScope = PageScope.First
                }
            ]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);
        var field = Assert.Single(loaded!.Fields);
        Assert.Equal(FieldKind.KeyValue, field.Kind);
        Assert.Equal(@"Invoice\s*No", field.KeyPattern);
        Assert.Equal(@"\d+", field.ValuePattern);
    }

    [Fact]
    public async Task Roundtrips_batch_level()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-level-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var profile = new IndexingProfile
        {
            Name = "Batch",
            Fields =
            [
                new IndexField { Name = "JobNo", Level = IndexLevel.Batch, Kind = FieldKind.Regex, ValuePattern = @"\d+" }
            ]
        };
        await store.SaveAsync(profile);
        var field = Assert.Single((await store.GetAsync(profile.Id))!.Fields);
        Assert.Equal(IndexLevel.Batch, field.Level);
    }

    [Fact]
    public async Task Roundtrips_text_and_lookup_fields()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-manual-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField { Name = "Due date", Kind = FieldKind.Text, Format = FieldFormat.Date },
                new IndexField
                {
                    Name = "Decision",
                    Kind = FieldKind.Lookup,
                    Format = FieldFormat.String,
                    LookupOptions =
                    [
                        new LookupOption { Key = "Approved", Value = "A" },
                        new LookupOption { Key = "Rejected", Value = "R" }
                    ],
                    LookupDefaultValue = "A"
                }
            ]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(FieldKind.Text, loaded!.Fields[0].Kind);
        Assert.Equal(FieldFormat.Date, loaded.Fields[0].Format);
        Assert.Equal(FieldKind.Lookup, loaded.Fields[1].Kind);
        Assert.Collection(
            loaded.Fields[1].LookupOptions,
            option => { Assert.Equal("Approved", option.Key); Assert.Equal("A", option.Value); },
            option => { Assert.Equal("Rejected", option.Key); Assert.Equal("R", option.Value); });
        Assert.Equal("A", loaded.Fields[1].LookupDefaultValue);
    }

    [Fact]
    public async Task Roundtrips_profile_level_scripts_and_a_script_kind_field()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-scripts-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var profile = new IndexingProfile
        {
            Name = "Scripted",
            Fields =
            [
                new IndexField { Name = "Computed", Kind = FieldKind.Script, ScriptExpression = "Fields[\"Other\"].Value.ToUpperInvariant()" }
            ],
            Scripts =
            [
                new FieldScript
                {
                    Name = "Enrich",
                    Enabled = true,
                    Trigger = ScriptTrigger.AfterFieldsPopulated,
                    Source = "Fields[\"Computed\"].Value = \"x\";",
                    TimeoutSeconds = 15
                }
            ]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        var field = Assert.Single(loaded!.Fields);
        Assert.Equal(FieldKind.Script, field.Kind);
        Assert.Equal("Fields[\"Other\"].Value.ToUpperInvariant()", field.ScriptExpression);

        var script = Assert.Single(loaded.Scripts);
        Assert.Equal("Enrich", script.Name);
        Assert.True(script.Enabled);
        Assert.Equal(ScriptTrigger.AfterFieldsPopulated, script.Trigger);
        Assert.Equal(15, script.TimeoutSeconds);
    }

    [Fact]
    public async Task Roundtrips_a_button_kind_field()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-button-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var profile = new IndexingProfile
        {
            Name = "Buttoned",
            Fields =
            [
                new IndexField
                {
                    Name = "Lookup customer",
                    Kind = FieldKind.Button,
                    ButtonLabel = "Look up customer",
                    ButtonScriptSource = "Fields[\"Customer Name\"].Value = \"ACME\";",
                    ButtonTimeoutSeconds = 20
                }
            ]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        var field = Assert.Single(loaded!.Fields);
        Assert.Equal(FieldKind.Button, field.Kind);
        Assert.Equal("Look up customer", field.ButtonLabel);
        Assert.Equal("Fields[\"Customer Name\"].Value = \"ACME\";", field.ButtonScriptSource);
        Assert.Equal(20, field.ButtonTimeoutSeconds);
    }

    [Fact]
    public async Task Roundtrips_a_profile_s_shared_script_source()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-shared-source-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var profile = new IndexingProfile
        {
            Name = "Shared helpers",
            SharedScriptSource = "static string Shout(string s) => s.ToUpperInvariant() + \"!\";"
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(profile.SharedScriptSource, loaded!.SharedScriptSource);
    }

    [Fact]
    public async Task An_old_profile_with_no_scripts_property_loads_with_an_empty_scripts_list()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-legacy-scripts-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var id = Guid.NewGuid();

        await WriteLegacyProfileJsonAsync(paths, id, """
            {
              "id": "REPLACE_ID",
              "name": "Pre-scripting profile",
              "fields": []
            }
            """);

        var loaded = await store.GetAsync(id);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Scripts);
    }

    [Fact]
    public async Task Migrates_legacy_split_on_blank_pages_into_separation()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-legacy-blank-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var id = Guid.NewGuid();

        await WriteLegacyProfileJsonAsync(paths, id, """
            {
              "id": "REPLACE_ID",
              "name": "Legacy blank split",
              "splitOnBlankPages": true,
              "fields": []
            }
            """);

        var loaded = await store.GetAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(DocumentSeparationTrigger.BlankPage, loaded!.Separation.Trigger);
        Assert.True(loaded.Separation.DiscardSeparatorPage);
    }

    [Fact]
    public async Task Migrates_legacy_barcode_separator_field_into_separation()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-legacy-barcode-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var id = Guid.NewGuid();
        var fieldId = Guid.NewGuid();

        await WriteLegacyProfileJsonAsync(paths, id, $$"""
            {
              "id": "REPLACE_ID",
              "name": "Legacy barcode split",
              "fields": [
                {
                  "id": "{{fieldId}}",
                  "name": "Separator",
                  "kind": "barcode",
                  "separatesDocuments": true,
                  "discardPage": true
                }
              ]
            }
            """);

        var loaded = await store.GetAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal(DocumentSeparationTrigger.Barcode, loaded!.Separation.Trigger);
        Assert.Equal(fieldId, loaded.Separation.BarcodeFieldId);
        Assert.True(loaded.Separation.DiscardSeparatorPage);
    }

    [Fact]
    public async Task Normalizes_an_untouched_legacy_barcode_page_scope_to_number()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-legacy-scope-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var id = Guid.NewGuid();

        // No pageScope/pageScopeConfigured keys at all — exactly what every barcode field saved before
        // PageScope became a real setting for barcode fields looks like.
        await WriteLegacyProfileJsonAsync(paths, id, """
            {
              "id": "REPLACE_ID",
              "name": "Legacy scope",
              "fields": [ { "name": "Ref", "kind": "barcode", "pageNumber": 2 } ]
            }
            """);

        var loaded = await store.GetAsync(id);

        var field = Assert.Single(loaded!.Fields);
        Assert.Equal(PageScope.Number, field.PageScope);
    }

    [Fact]
    public async Task Preserves_a_deliberately_chosen_first_page_scope_for_a_barcode_field()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-configured-scope-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var id = Guid.NewGuid();

        await WriteLegacyProfileJsonAsync(paths, id, """
            {
              "id": "REPLACE_ID",
              "name": "Configured scope",
              "fields": [ { "name": "Ref", "kind": "barcode", "pageScope": "first", "pageScopeConfigured": true } ]
            }
            """);

        var loaded = await store.GetAsync(id);

        var field = Assert.Single(loaded!.Fields);
        Assert.Equal(PageScope.First, field.PageScope);
    }

    private static async Task WriteLegacyProfileJsonAsync(AppPaths paths, Guid id, string json)
    {
        var path = paths.ProfileJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json.Replace("REPLACE_ID", id.ToString()));
    }
}
