using Capture.App.Converters;

namespace Capture.Tests;

// Drives which of a Table mode field cell's two halves — the read-only TextBlock, or the inline
// editor — is visible: exactly one of Editing/NotEditing must be true for a given cell at a time. See
// MainWindow.axaml.cs's BuildIndexColumn and DocumentRow.EditingField.
public class IndexCellIsEditingConverterTests
{
    [Fact]
    public void Editing_is_true_only_for_the_matching_field()
    {
        var thisField = new IndexCellBinding("Invoice No", IsBatchField: false);
        var otherField = new IndexCellBinding("Supplier", IsBatchField: false);

        Assert.Equal(true, IndexCellIsEditingConverter.Editing.Convert(thisField, typeof(bool), thisField, default!));
        Assert.Equal(false, IndexCellIsEditingConverter.Editing.Convert(thisField, typeof(bool), otherField, default!));
        Assert.Equal(false, IndexCellIsEditingConverter.Editing.Convert(null, typeof(bool), thisField, default!));
    }

    [Fact]
    public void NotEditing_is_the_exact_inverse_of_Editing()
    {
        var thisField = new IndexCellBinding("Invoice No", IsBatchField: false);
        var otherField = new IndexCellBinding("Supplier", IsBatchField: false);

        Assert.Equal(false, IndexCellIsEditingConverter.NotEditing.Convert(thisField, typeof(bool), thisField, default!));
        Assert.Equal(true, IndexCellIsEditingConverter.NotEditing.Convert(thisField, typeof(bool), otherField, default!));
        Assert.Equal(true, IndexCellIsEditingConverter.NotEditing.Convert(null, typeof(bool), thisField, default!));
    }

    [Fact]
    public void A_batch_field_and_a_document_field_with_the_same_name_are_distinct()
    {
        var batchField = new IndexCellBinding("Invoice No", IsBatchField: true);
        var documentField = new IndexCellBinding("Invoice No", IsBatchField: false);

        Assert.Equal(false, IndexCellIsEditingConverter.Editing.Convert(batchField, typeof(bool), documentField, default!));
    }
}
