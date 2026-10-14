using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Storage;

namespace Capture.Tests;

public class CompletedExportsPersistenceTests
{
    [Fact]
    public async Task A_documents_completed_exports_are_saved_updated_and_cleared()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-completed-exports-" + Guid.NewGuid().ToString("N")));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var document = new CaptureDocument { OriginalFileName = "doc.pdf", StoredPath = "/tmp/doc.pdf", CompletedExportIds = [first] };
        await store.SaveAsync(document, []);

        Assert.Equal([first], (await store.GetAsync(document.Id))!.CompletedExportIds);

        document.CompletedExportIds.Add(second);
        await store.UpdateAsync(document);
        Assert.Equal([first, second], (await new SqliteDocumentStore(paths).GetAsync(document.Id))!.CompletedExportIds);

        document.CompletedExportIds.Clear();
        await store.UpdateAsync(document);
        Assert.Empty((await store.GetAsync(document.Id))!.CompletedExportIds);
        Assert.Empty(Assert.Single(await store.GetAllAsync()).CompletedExportIds);
    }
}

public class ExportHistoryStoreTests
{
    [Fact]
    public async Task Export_runs_are_kept_newest_first_and_removed_with_the_document()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-export-history-" + Guid.NewGuid().ToString("N")));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();
        var document = new CaptureDocument { OriginalFileName = "doc.pdf", StoredPath = "/tmp/doc.pdf" };
        var other = new CaptureDocument { OriginalFileName = "other.pdf", StoredPath = "/tmp/other.pdf" };
        await store.SaveAsync(document, []);
        await store.SaveAsync(other, []);
        var export = Guid.NewGuid();
        var earlier = new DateTimeOffset(2026, 10, 14, 1, 0, 0, TimeSpan.Zero);

        await store.AddAsync(document.Id,
            [new ExportRecord(export, "Archive", Capture.Core.Profiles.ExportType.Therefore, earlier, false, null, "Tenant name is required.")]);
        await store.AddAsync(document.Id,
            [new ExportRecord(export, "Archive", Capture.Core.Profiles.ExportType.Therefore, earlier.AddMinutes(5), true, "Therefore document 1234", null)]);

        var history = await store.GetAsync([document.Id, other.Id]);
        var records = history[document.Id];
        Assert.Equal(2, records.Count);
        Assert.True(records[0].Success);
        Assert.Equal("Therefore document 1234", records[0].Reference);
        Assert.Equal("Tenant name is required.", records[1].Message);
        Assert.False(history.ContainsKey(other.Id));

        await store.PurgeAsync(document.Id);
        Assert.Empty(await store.GetAsync([document.Id]));
    }
}
