using Capture.App.ViewModels;
using Capture.Core.Models;

namespace Capture.Tests;

// MainViewModel.ApplyBatchDividers backs the "Batch 41 · Scanner · 3 docs" divider row shown above the
// first document of each batch, in both Table mode (per profile group) and the Preview mode Inbox rail
// (the flat master order) — see MainViewModel.Documents.cs.
public class BatchDividerTests
{
    private static DocumentRow Row(Guid? batchId) => new(new CaptureDocument
    {
        OriginalFileName = "doc.pdf",
        StoredPath = "/tmp/doc.pdf",
        BatchId = batchId
    });

    [Fact]
    public void Marks_only_the_first_row_of_each_batch()
    {
        var batchA = Guid.NewGuid();
        var batchB = Guid.NewGuid();
        var rows = new[] { Row(batchA), Row(batchA), Row(batchB), Row(batchB), Row(batchB) };

        MainViewModel.ApplyBatchDividers(rows, new Dictionary<Guid, CaptureBatch>());

        Assert.Equal([true, false, true, false, false], rows.Select(row => row.IsFirstInBatch));
    }

    [Fact]
    public void Populates_the_divider_label_from_batch_metadata_and_a_count_within_the_given_ordering()
    {
        var batchId = Guid.NewGuid();
        var batches = new Dictionary<Guid, CaptureBatch>
        {
            [batchId] = new CaptureBatch { Id = batchId, Number = 41, InputChannel = "Scanner" }
        };
        var rows = new[] { Row(batchId), Row(batchId), Row(batchId) };

        MainViewModel.ApplyBatchDividers(rows, batches);

        var first = rows[0];
        Assert.Equal(41, first.BatchNumber);
        Assert.Equal("Scanner", first.BatchInputChannel);
        Assert.Equal(3, first.BatchDocumentCount);
        Assert.Equal("Batch 41 · Scanner · 3 docs", first.BatchDividerLabel);
    }

    [Fact]
    public void Singular_document_count_reads_as_1_doc_not_1_docs()
    {
        var batchId = Guid.NewGuid();
        var batches = new Dictionary<Guid, CaptureBatch>
        {
            [batchId] = new CaptureBatch { Id = batchId, Number = 7 }
        };
        var rows = new[] { Row(batchId) };

        MainViewModel.ApplyBatchDividers(rows, batches);

        Assert.Equal("Batch 7 · 1 doc", rows[0].BatchDividerLabel);
    }

    [Fact]
    public void A_batch_missing_from_the_lookup_still_marks_first_row_but_leaves_the_label_blank()
    {
        var batchId = Guid.NewGuid();
        var rows = new[] { Row(batchId) };

        MainViewModel.ApplyBatchDividers(rows, new Dictionary<Guid, CaptureBatch>());

        Assert.True(rows[0].IsFirstInBatch);
        Assert.Equal(string.Empty, rows[0].BatchDividerLabel);
    }

    [Fact]
    public void A_null_batch_id_is_never_marked_as_a_divider()
    {
        var rows = new[] { Row(null), Row(null) };

        MainViewModel.ApplyBatchDividers(rows, new Dictionary<Guid, CaptureBatch>());

        Assert.All(rows, row => Assert.False(row.IsFirstInBatch));
    }

    [Fact]
    public void BuildDisplayRows_splices_a_standalone_divider_before_each_batchs_first_document()
    {
        var batchA = Guid.NewGuid();
        var batchB = Guid.NewGuid();
        var rows = new[] { Row(batchA), Row(batchA), Row(batchB) };
        var batches = new Dictionary<Guid, CaptureBatch>
        {
            [batchA] = new CaptureBatch { Id = batchA, Number = 1 },
            [batchB] = new CaptureBatch { Id = batchB, Number = 2 }
        };
        MainViewModel.ApplyBatchDividers(rows, batches);

        var display = MainViewModel.BuildDisplayRows(rows);

        // Divider, doc, doc, divider, doc — never two dividers or two same-batch docs without one.
        Assert.Collection(display,
            item => Assert.Equal(batchA, Assert.IsType<BatchDividerRow>(item).BatchId),
            item => Assert.Same(rows[0], item),
            item => Assert.Same(rows[1], item),
            item => Assert.Equal(batchB, Assert.IsType<BatchDividerRow>(item).BatchId),
            item => Assert.Same(rows[2], item));
    }

    [Fact]
    public void BuildDisplayRows_adds_no_divider_for_a_document_outside_any_batch()
    {
        var rows = new[] { Row(null) };
        MainViewModel.ApplyBatchDividers(rows, new Dictionary<Guid, CaptureBatch>());

        var display = MainViewModel.BuildDisplayRows(rows);

        Assert.Same(rows[0], Assert.Single(display));
    }
}
