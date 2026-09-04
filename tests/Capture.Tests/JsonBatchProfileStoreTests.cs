using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public class JsonBatchProfileStoreTests
{
    [Fact]
    public async Task Roundtrips_batch_profile_with_multiple_strategies_and_match_mode()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-batch-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonBatchProfileStore(paths);
        var indexingProfileId = Guid.NewGuid();

        var profile = new BatchProfile
        {
            Name = "Multi-strategy batching",
            SampleFileName = "sample.pdf",
            Mode = BatchMode.UseStrategies,
            MatchMode = SeparationMatchMode.AtLeast,
            MatchMinimum = 2,
            IndexingProfileId = indexingProfileId,
            Strategies =
            [
                new SeparationStrategy
                {
                    Type = SeparationStrategyType.Barcode,
                    Name = "Cover barcode",
                    Zone = new ZoneRect { PageNumber = 1, X = 0.1f, Y = 0.2f, Width = 0.3f, Height = 0.05f },
                    ZonePageNumber = 1,
                    BarcodeFormat = "CODE_128",
                    BarcodeValuePattern = "^BATCH-",
                    DiscardSeparatorPage = true
                },
                new SeparationStrategy
                {
                    Type = SeparationStrategyType.OcrZone,
                    Zone = new ZoneRect { PageNumber = 1, X = 0f, Y = 0f, Width = 0.3f, Height = 0.3f },
                    TextPattern = "^COVER$"
                },
                new SeparationStrategy
                {
                    Type = SeparationStrategyType.EveryNPages,
                    PageCount = 5
                }
            ]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Multi-strategy batching", loaded!.Name);
        Assert.Equal("sample.pdf", loaded.SampleFileName);
        Assert.Equal(BatchMode.UseStrategies, loaded.Mode);
        Assert.Equal(SeparationMatchMode.AtLeast, loaded.MatchMode);
        Assert.Equal(2, loaded.MatchMinimum);
        Assert.Equal(indexingProfileId, loaded.IndexingProfileId);
        Assert.Equal(3, loaded.Strategies.Count);

        var barcode = loaded.Strategies[0];
        Assert.Equal(SeparationStrategyType.Barcode, barcode.Type);
        Assert.Equal("Cover barcode", barcode.Name);
        Assert.NotNull(barcode.Zone);
        Assert.Equal(0.1f, barcode.Zone!.X);
        Assert.Equal("CODE_128", barcode.BarcodeFormat);
        Assert.Equal("^BATCH-", barcode.BarcodeValuePattern);
        Assert.True(barcode.DiscardSeparatorPage);

        var ocrZone = loaded.Strategies[1];
        Assert.Equal(SeparationStrategyType.OcrZone, ocrZone.Type);
        Assert.NotNull(ocrZone.Zone);
        Assert.Equal("^COVER$", ocrZone.TextPattern);

        var everyNPages = loaded.Strategies[2];
        Assert.Equal(SeparationStrategyType.EveryNPages, everyNPages.Type);
        Assert.Equal(5, everyNPages.PageCount);
    }

    [Fact]
    public async Task Roundtrips_an_empty_strategies_list_with_NewBatchPerFile_mode()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-batch-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonBatchProfileStore(paths);

        var profile = new BatchProfile { Name = "Default" };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(BatchMode.NewBatchPerFile, loaded!.Mode);
        Assert.Empty(loaded.Strategies);
        Assert.Equal(SeparationMatchMode.Any, loaded.MatchMode);
        Assert.Null(loaded.IndexingProfileId);
    }

    [Fact]
    public async Task GetAllAsync_returns_every_saved_profile_ordered_by_name()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-batch-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonBatchProfileStore(paths);

        await store.SaveAsync(new BatchProfile { Name = "Zebra" });
        await store.SaveAsync(new BatchProfile { Name = "Alpha" });

        var all = await store.GetAllAsync();

        Assert.Equal(["Alpha", "Zebra"], all.Select(profile => profile.Name));
    }

    [Fact]
    public async Task DeleteAsync_removes_the_profile()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-batch-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonBatchProfileStore(paths);
        var profile = new BatchProfile { Name = "Temp" };
        await store.SaveAsync(profile);

        await store.DeleteAsync(profile.Id);

        Assert.Null(await store.GetAsync(profile.Id));
    }
}
