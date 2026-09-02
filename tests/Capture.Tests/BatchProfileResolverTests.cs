using Capture.Core.Batches;

namespace Capture.Tests;

/// <summary>Covers what MainViewModel's import paths actually hand BatchAllocator when nothing selects
/// a real BatchProfile — see WatchSettings.NoBatchProfileBehavior.</summary>
public class BatchProfileResolverTests
{
    [Fact]
    public void A_selected_profile_always_wins_regardless_of_the_no_profile_setting()
    {
        var selected = new BatchProfile { Trigger = BatchTrigger.EveryNPages, PageCount = 5 };

        var resolved = BatchProfileResolver.Resolve(selected, NoBatchProfileBehavior.AddToOpenBatch);

        Assert.Same(selected, resolved);
    }

    [Fact]
    public void No_selection_with_NewBatchPerFile_behavior_synthesizes_a_new_batch_per_file_profile()
    {
        var resolved = BatchProfileResolver.Resolve(null, NoBatchProfileBehavior.NewBatchPerFile);

        Assert.NotNull(resolved);
        Assert.Equal(BatchTrigger.NewBatchPerFile, resolved!.Trigger);
    }

    [Fact]
    public void No_selection_with_AddToOpenBatch_behavior_resolves_to_null()
    {
        // null is exactly what BatchAllocator already treats as "keep appending to the open batch" —
        // see BatchAllocatorTests.No_profile_keeps_documents_from_every_file_in_one_batch.
        var resolved = BatchProfileResolver.Resolve(null, NoBatchProfileBehavior.AddToOpenBatch);

        Assert.Null(resolved);
    }
}
