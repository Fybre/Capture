using Capture.Core.Import;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public class JsonImportProfileStoreTests
{
    [Fact]
    public async Task Roundtrips_import_profile_with_barcode_zone()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-import-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonImportProfileStore(paths);
        var indexingProfileId = Guid.NewGuid();

        var profile = new ImportProfile
        {
            Name = "Barcode splits",
            Trigger = ImportSeparationTrigger.Barcode,
            SampleFileName = "sample.pdf",
            BarcodeZone = new ZoneRect { PageNumber = 1, X = 0.1f, Y = 0.2f, Width = 0.3f, Height = 0.05f },
            BarcodePageNumber = 1,
            BarcodeFormat = "CODE_128",
            BarcodeValuePattern = "^DOC-",
            DiscardSeparatorPage = true,
            IndexingProfileIds = [indexingProfileId]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Barcode splits", loaded!.Name);
        Assert.Equal(ImportSeparationTrigger.Barcode, loaded.Trigger);
        Assert.Equal("sample.pdf", loaded.SampleFileName);
        Assert.NotNull(loaded.BarcodeZone);
        Assert.Equal(0.1f, loaded.BarcodeZone!.X);
        Assert.Equal("CODE_128", loaded.BarcodeFormat);
        Assert.Equal("^DOC-", loaded.BarcodeValuePattern);
        Assert.True(loaded.DiscardSeparatorPage);
        Assert.Equal([indexingProfileId], loaded.IndexingProfileIds);
    }

    [Fact]
    public async Task GetAllAsync_returns_every_saved_profile_ordered_by_name()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-import-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonImportProfileStore(paths);

        await store.SaveAsync(new ImportProfile { Name = "Zebra" });
        await store.SaveAsync(new ImportProfile { Name = "Alpha" });

        var all = await store.GetAllAsync();

        Assert.Equal(["Alpha", "Zebra"], all.Select(profile => profile.Name));
    }

    [Fact]
    public async Task DeleteAsync_removes_the_profile()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-import-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonImportProfileStore(paths);
        var profile = new ImportProfile { Name = "Temp" };
        await store.SaveAsync(profile);

        await store.DeleteAsync(profile.Id);

        Assert.Null(await store.GetAsync(profile.Id));
    }
}
