using Capture.Core.Import;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public class JsonImportProfileStoreTests
{
    [Fact]
    public async Task Roundtrips_import_profile_with_multiple_strategies_and_match_mode()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-import-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonImportProfileStore(paths);
        var indexingProfileId = Guid.NewGuid();
        var batchProfileId = Guid.NewGuid();
        var defaultIndexingProfileId = Guid.NewGuid();

        var profile = new ImportProfile
        {
            Name = "Multi-strategy",
            SampleFileName = "sample.pdf",
            MatchMode = SeparationMatchMode.AtLeast,
            MatchMinimum = 2,
            IndexingProfileIds = [indexingProfileId],
            BatchProfileId = batchProfileId,
            DefaultIndexingProfileId = defaultIndexingProfileId,
            Strategies =
            [
                new SeparationStrategy
                {
                    Type = SeparationStrategyType.Barcode,
                    Name = "Cover barcode",
                    Zone = new ZoneRect { PageNumber = 1, X = 0.1f, Y = 0.2f, Width = 0.3f, Height = 0.05f },
                    ZonePageNumber = 1,
                    BarcodeFormat = "CODE_128",
                    BarcodeValuePattern = "^DOC-",
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
                    Type = SeparationStrategyType.Similarity,
                    ReferenceEmbedding = [0.1f, 0.2f, 0.3f],
                    SimilarityThreshold = 0.9
                }
            ]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Multi-strategy", loaded!.Name);
        Assert.Equal("sample.pdf", loaded.SampleFileName);
        Assert.Equal(SeparationMatchMode.AtLeast, loaded.MatchMode);
        Assert.Equal(2, loaded.MatchMinimum);
        Assert.Equal([indexingProfileId], loaded.IndexingProfileIds);
        Assert.Equal(batchProfileId, loaded.BatchProfileId);
        Assert.Equal(defaultIndexingProfileId, loaded.DefaultIndexingProfileId);
        Assert.Equal(3, loaded.Strategies.Count);

        var barcode = loaded.Strategies[0];
        Assert.Equal(SeparationStrategyType.Barcode, barcode.Type);
        Assert.Equal("Cover barcode", barcode.Name);
        Assert.NotNull(barcode.Zone);
        Assert.Equal(0.1f, barcode.Zone!.X);
        Assert.Equal("CODE_128", barcode.BarcodeFormat);
        Assert.Equal("^DOC-", barcode.BarcodeValuePattern);
        Assert.True(barcode.DiscardSeparatorPage);

        var ocrZone = loaded.Strategies[1];
        Assert.Equal(SeparationStrategyType.OcrZone, ocrZone.Type);
        Assert.NotNull(ocrZone.Zone);
        Assert.Equal("^COVER$", ocrZone.TextPattern);

        var similarity = loaded.Strategies[2];
        Assert.Equal(SeparationStrategyType.Similarity, similarity.Type);
        Assert.NotNull(similarity.ReferenceEmbedding);
        Assert.Equal([0.1f, 0.2f, 0.3f], similarity.ReferenceEmbedding!);
        Assert.Equal(0.9, similarity.SimilarityThreshold);
    }

    [Fact]
    public async Task Roundtrips_an_empty_strategies_list_and_null_reference_embedding()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-import-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonImportProfileStore(paths);

        var profile = new ImportProfile { Name = "No strategies yet" };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Strategies);
        Assert.Equal(SeparationMatchMode.Any, loaded.MatchMode);
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
