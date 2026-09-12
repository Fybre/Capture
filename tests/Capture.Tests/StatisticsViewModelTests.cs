using Capture.App.ViewModels;
using Capture.Core.CaptureProfiles;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Store;
using Capture.Storage;

namespace Capture.Tests;

public class StatisticsViewModelTests
{
    [Fact]
    public async Task LoadAsync_aggregates_totals_status_and_document_type_volume()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-stats-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var invoiceType = new DocumentTypeDefinition { Name = "Invoice" };
        var profile = new CaptureProfile { Name = "Accounts", DocumentTypes = [invoiceType] };
        var profileStore = new FakeProfileStore(profile);

        // Two Ready invoices, one NeedsReview with no profile, one already exported and one trashed.
        var readyOne = NewDocument(invoiceType.Id, DocumentStatus.Ready);
        var readyTwo = NewDocument(invoiceType.Id, DocumentStatus.Ready);
        var needsReview = NewDocument(null, DocumentStatus.NeedsReview);
        var exported = NewDocument(invoiceType.Id, DocumentStatus.Exported);
        exported.ExportedUtc = DateTimeOffset.UtcNow;
        await store.SaveAsync(readyOne, []);
        await store.SaveAsync(readyTwo, []);
        await store.SaveAsync(needsReview, []);
        await store.SaveAsync(exported, []);

        var trashed = NewDocument(invoiceType.Id, DocumentStatus.Ready);
        await store.SaveAsync(trashed, []);
        await store.SoftDeleteAsync(trashed.Id);

        var viewModel = new StatisticsViewModel(store, profileStore);
        await viewModel.LoadAsync();

        Assert.Equal(4, viewModel.TotalInInbox);
        Assert.Equal(1, viewModel.TotalInTrash);
        Assert.Equal(1, viewModel.TotalExportedAllTime);

        var ready = Assert.Single(viewModel.StatusBreakdown, bar => bar.Label == "Ready");
        Assert.Equal(2, ready.Count);
        var needsReviewBar = Assert.Single(viewModel.StatusBreakdown, bar => bar.Label == "Needs review");
        Assert.Equal(1, needsReviewBar.Count);

        var invoiceBar = Assert.Single(viewModel.TopDocumentTypes, bar => bar.Label == "Invoice");
        // Includes the trashed one too — "top document types" counts everything ever captured.
        Assert.Equal(4, invoiceBar.Count);
        var noProfileBar = Assert.Single(viewModel.TopDocumentTypes, bar => bar.Label == "No profile applied");
        Assert.Equal(1, noProfileBar.Count);

        Assert.Equal(30, viewModel.CapturedPerDay.Count);
        Assert.Equal(5, viewModel.CapturedPerDay.Sum(bar => bar.Count));
        Assert.True(viewModel.HasExportActivity);
        Assert.Equal(1, viewModel.ExportedPerDay.Sum(bar => bar.Count));
    }

    private static CaptureDocument NewDocument(Guid? profileId, DocumentStatus status) => new()
    {
        OriginalFileName = "doc.pdf",
        StoredPath = "/tmp/doc.pdf",
        Source = DocumentSource.Import,
        ProfileId = profileId,
        Status = status,
        PageCount = 1
    };

    private sealed class FakeProfileStore(params CaptureProfile[] profiles) : ICaptureProfileStore
    {
        public Task<IReadOnlyList<CaptureProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureProfile>>(profiles);
        public Task<CaptureProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
        public Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
