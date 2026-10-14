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
