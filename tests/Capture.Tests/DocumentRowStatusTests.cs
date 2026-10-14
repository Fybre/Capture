using Capture.App.ViewModels;
using Capture.Core.Models;

namespace Capture.Tests;

public class DocumentRowStatusTests
{
    private static IndexValue Filled() => new() { FieldId = Guid.NewGuid(), FieldName = "Invoice No", Value = "INV-1", Confidence = 99 };

    private static IndexValue Missing() => new() { FieldId = Guid.NewGuid(), FieldName = "Invoice No", Value = "", Mandatory = true };

    // Loading an Exported document's fields used to turn it back into Ready, so Export all sent it again.
    [Fact]
    public void Loading_an_exported_documents_fields_keeps_it_exported()
    {
        var row = new DocumentRow(new CaptureDocument { OriginalFileName = "a.pdf", StoredPath = "/tmp/a.pdf", Status = DocumentStatus.Exported });

        row.SetDocumentIndexes([Filled()]);
        row.SetBatchIndexes([]);

        Assert.Equal(DocumentStatus.Exported, row.Document.Status);
    }

    [Theory]
    [InlineData(DocumentStatus.NeedsReview)]
    [InlineData(DocumentStatus.Ready)]
    public void A_document_being_reviewed_still_follows_its_fields(DocumentStatus status)
    {
        var row = new DocumentRow(new CaptureDocument { OriginalFileName = "a.pdf", StoredPath = "/tmp/a.pdf", Status = status });

        row.SetDocumentIndexes([Filled()]);
        Assert.Equal(DocumentStatus.Ready, row.Document.Status);

        row.SetDocumentIndexes([Missing()]);
        Assert.Equal(DocumentStatus.NeedsReview, row.Document.Status);
    }
}
