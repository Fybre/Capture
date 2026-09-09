using Capture.Core.Paths;
using Capture.Core.Redaction;
using Capture.Storage;

namespace Capture.Tests;

public class JsonRedactionEntitySetStoreTests
{
    private static (AppPaths Paths, JsonRedactionEntitySetStore Store) CreateStore()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-redaction-sets-" + Guid.NewGuid().ToString("N")));
        return (paths, new JsonRedactionEntitySetStore(paths));
    }

    [Fact]
    public async Task Roundtrips_a_custom_set()
    {
        var (_, store) = CreateStore();
        var set = new RedactionEntitySet { Name = "Core + NZ", Entities = ["PERSON", "EMAIL_ADDRESS", "AU_TFN"] };

        await store.SaveAsync(set);
        var loaded = await store.GetAsync(set.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Core + NZ", loaded!.Name);
        Assert.Equal(set.Entities, loaded.Entities);
    }

    [Fact]
    public async Task GetAllAsync_returns_every_saved_set_ordered_by_name()
    {
        var (_, store) = CreateStore();
        await store.SaveAsync(new RedactionEntitySet { Name = "Zebra", Entities = ["PERSON"] });
        await store.SaveAsync(new RedactionEntitySet { Name = "Alpha", Entities = ["EMAIL_ADDRESS"] });

        var all = await store.GetAllAsync();

        Assert.Equal(["Alpha", "Zebra"], all.Select(set => set.Name));
    }

    [Fact]
    public async Task DeleteAsync_removes_the_set()
    {
        var (_, store) = CreateStore();
        var set = new RedactionEntitySet { Name = "Temp", Entities = ["PERSON"] };
        await store.SaveAsync(set);

        await store.DeleteAsync(set.Id);

        Assert.Null(await store.GetAsync(set.Id));
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_unknown_id()
    {
        var (_, store) = CreateStore();

        Assert.Null(await store.GetAsync(Guid.NewGuid()));
    }
}
