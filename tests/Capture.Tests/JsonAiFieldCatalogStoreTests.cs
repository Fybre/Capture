using Capture.Core.Indexing;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public class JsonAiFieldCatalogStoreTests
{
    [Fact]
    public async Task Bootstraps_default_catalog_file_on_first_load()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-ai-catalog-" + Guid.NewGuid().ToString("N")));
        var store = new JsonAiFieldCatalogStore(paths);

        Assert.False(File.Exists(paths.AiFieldCatalogPath));

        var types = await store.LoadAsync();

        Assert.True(File.Exists(paths.AiFieldCatalogPath));
        Assert.Equal(AiFieldCatalog.DefaultTypes.Count, types.Count);
        Assert.Contains(types, item => item.Id == AiFieldCatalog.CustomTypeId);
    }

    [Fact]
    public async Task Loads_user_added_field_types_from_disk()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-ai-catalog-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.AiFieldCatalogPath, """
            [
              { "id": "shipping.container", "classification": "Shipping", "name": "Container No", "format": "string", "hint": "Container number on the manifest." }
            ]
            """);
        var store = new JsonAiFieldCatalogStore(paths);

        var types = await store.LoadAsync();

        Assert.Single(types);
        Assert.Equal("Shipping", types[0].Classification);
        Assert.Equal(FieldFormat.String, types[0].Format);
    }

    [Fact]
    public async Task Falls_back_to_defaults_when_the_file_is_malformed()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-ai-catalog-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.AiFieldCatalogPath, "{ not valid json");
        var store = new JsonAiFieldCatalogStore(paths);

        var types = await store.LoadAsync();

        Assert.Equal(AiFieldCatalog.DefaultTypes.Count, types.Count);
    }
}

[Collection("AiFieldCatalog")]
public class AiFieldCatalogLoadTests
{
    [Fact]
    public void Load_replaces_the_catalog_and_keeps_a_custom_entry()
    {
        try
        {
            AiFieldCatalog.Load([new AiFieldType("shipping.container", "Shipping", "Container No", FieldFormat.String, "Container number.")]);

            Assert.Equal(2, AiFieldCatalog.All.Count);
            Assert.Contains(AiFieldCatalog.All, item => item.Id == AiFieldCatalog.CustomTypeId);
            Assert.Contains(AiFieldCatalog.All, item => item.Id == "shipping.container");
            Assert.Contains("Shipping", AiFieldCatalog.Classifications);
        }
        finally
        {
            AiFieldCatalog.Load(null);
        }
    }

    [Fact]
    public void Load_with_empty_list_falls_back_to_defaults()
    {
        try
        {
            AiFieldCatalog.Load([]);

            Assert.Equal(AiFieldCatalog.DefaultTypes.Count, AiFieldCatalog.All.Count);
        }
        finally
        {
            AiFieldCatalog.Load(null);
        }
    }

    [Fact]
    public async Task Adds_new_built_in_types_to_an_older_catalog_once()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-ai-catalog-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.AiFieldCatalogPath, """
            [
              { "id": "invoice.number", "classification": "Invoice", "name": "Invoice No", "format": "string", "hint": "Edited hint." },
              { "id": "invoice.total", "classification": "Invoice", "name": "Invoice Total", "format": "money", "hint": "" },
              { "id": "letter.date", "classification": "Correspondence", "name": "Letter Date", "format": "date", "hint": "" }
            ]
            """);
        var store = new JsonAiFieldCatalogStore(paths);

        var types = await store.LoadAsync();

        Assert.Equal(["invoice.number", "invoice.total", "invoice.abn", "invoice.net", "letter.date"], types.Select(item => item.Id));
        Assert.Equal("Edited hint.", types[0].Hint);

        // Deleting a newly added type keeps it deleted.
        await File.WriteAllTextAsync(paths.AiFieldCatalogPath, """
            [ { "id": "invoice.number", "classification": "Invoice", "name": "Invoice No", "format": "string", "hint": "" } ]
            """);
        types = await store.LoadAsync();
        Assert.Equal(["invoice.number"], types.Select(item => item.Id));
    }

    [Fact]
    public async Task New_built_in_types_skip_classifications_the_catalog_no_longer_uses()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-ai-catalog-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.AiFieldCatalogPath, """
            [ { "id": "shipping.container", "classification": "Shipping", "name": "Container No", "format": "string", "hint": "" } ]
            """);

        var types = await new JsonAiFieldCatalogStore(paths).LoadAsync();

        Assert.Equal(["shipping.container"], types.Select(item => item.Id));
    }
}
