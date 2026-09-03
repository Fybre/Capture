using Capture.Core.Models;
using Capture.Core.Store;

namespace Capture.Tests;

/// <summary>Covers the selection rule behind Settings' "Clean up now" — see SettingsViewModel's
/// CleanUpOldDocumentsAsync, which just deletes whatever this returns.</summary>
public class DocumentCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static CaptureDocument Document(DocumentStatus status, int daysOld) => new()
    {
        OriginalFileName = "doc.pdf",
        StoredPath = "/tmp/doc.pdf",
        Status = status,
        CreatedUtc = Now.AddDays(-daysOld)
    };

    [Fact]
    public void An_old_exported_document_is_selected()
    {
        var stale = DocumentCleanup.SelectStale([Document(DocumentStatus.Exported, daysOld: 40)], olderThanDays: 30, Now);

        Assert.Single(stale);
    }

    [Fact]
    public void A_recently_exported_document_is_not_selected()
    {
        var stale = DocumentCleanup.SelectStale([Document(DocumentStatus.Exported, daysOld: 5)], olderThanDays: 30, Now);

        Assert.Empty(stale);
    }

    [Theory]
    [InlineData(DocumentStatus.Queued)]
    [InlineData(DocumentStatus.Processing)]
    [InlineData(DocumentStatus.NeedsReview)]
    [InlineData(DocumentStatus.Ready)]
    [InlineData(DocumentStatus.Error)]
    public void A_document_that_hasn_t_finished_or_failed_is_never_selected_regardless_of_age(DocumentStatus status)
    {
        var stale = DocumentCleanup.SelectStale([Document(status, daysOld: 9999)], olderThanDays: 30, Now);

        Assert.Empty(stale);
    }

    [Fact]
    public void A_zero_or_negative_day_threshold_is_clamped_to_at_least_one_day()
    {
        // Guards against an accidental "0 days" wiping out documents exported earlier today.
        var stale = DocumentCleanup.SelectStale([Document(DocumentStatus.Exported, daysOld: 0)], olderThanDays: 0, Now);

        Assert.Empty(stale);
    }

    [Fact]
    public void SelectExported_ignores_age_entirely()
    {
        var exported = DocumentCleanup.SelectExported([
            Document(DocumentStatus.Exported, daysOld: 0),
            Document(DocumentStatus.Exported, daysOld: 9999)
        ]);

        Assert.Equal(2, exported.Count);
    }

    [Theory]
    [InlineData(DocumentStatus.Queued)]
    [InlineData(DocumentStatus.Processing)]
    [InlineData(DocumentStatus.NeedsReview)]
    [InlineData(DocumentStatus.Ready)]
    [InlineData(DocumentStatus.Error)]
    public void SelectExported_still_never_includes_a_document_that_isn_t_exported(DocumentStatus status)
    {
        var exported = DocumentCleanup.SelectExported([Document(status, daysOld: 9999)]);

        Assert.Empty(exported);
    }

    private static CaptureDocument Trashed(int deletedDaysAgo) => new()
    {
        OriginalFileName = "doc.pdf",
        StoredPath = "/tmp/doc.pdf",
        Status = DocumentStatus.Exported,
        CreatedUtc = Now.AddDays(-100),
        DeletedUtc = Now.AddDays(-deletedDaysAgo)
    };

    [Fact]
    public void SelectExpiredTrash_selects_a_document_deleted_past_the_retention_window()
    {
        var expired = DocumentCleanup.SelectExpiredTrash([Trashed(deletedDaysAgo: 40)], retentionDays: 30, Now);

        Assert.Single(expired);
    }

    [Fact]
    public void SelectExpiredTrash_does_not_select_a_recently_trashed_document()
    {
        var expired = DocumentCleanup.SelectExpiredTrash([Trashed(deletedDaysAgo: 5)], retentionDays: 30, Now);

        Assert.Empty(expired);
    }

    [Fact]
    public void SelectExpiredTrash_ignores_a_document_that_was_never_trashed()
    {
        // Defensive: only called against GetTrashedAsync's results in practice, but shouldn't blow up
        // or misbehave if handed an active (DeletedUtc == null) document.
        var expired = DocumentCleanup.SelectExpiredTrash([Document(DocumentStatus.Exported, daysOld: 9999)], retentionDays: 30, Now);

        Assert.Empty(expired);
    }

    [Fact]
    public void SelectExpiredTrash_clamps_a_zero_or_negative_retention_to_at_least_one_day()
    {
        var expired = DocumentCleanup.SelectExpiredTrash([Trashed(deletedDaysAgo: 0)], retentionDays: 0, Now);

        Assert.Empty(expired);
    }
}
