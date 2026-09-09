using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Storage;

namespace Capture.Tests;

public class OpenBatchPersistenceTests
{
    [Fact]
    public async Task Open_batch_is_resolved_by_profile_and_channel_across_store_instances()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-open-batch-" + Guid.NewGuid().ToString("N")));
        var profileId = Guid.NewGuid();
        var first = new SqliteDocumentStore(paths);
        await first.InitializeAsync();
        var created = await first.CreateScopedBatchAsync(profileId, "watch:invoices");

        var afterRestart = new SqliteDocumentStore(paths);
        await afterRestart.InitializeAsync();
        var resolved = await afterRestart.GetOpenBatchAsync(profileId, "watch:invoices");

        Assert.Equal(created.Id, resolved?.Id);
        Assert.Null(await afterRestart.GetOpenBatchAsync(profileId, "manual"));
        await afterRestart.SetBatchStateAsync(created.Id, BatchState.Closed);
        Assert.Null(await afterRestart.GetOpenBatchAsync(profileId, "watch:invoices"));
    }
}
