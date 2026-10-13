using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class ManualValueCarryOverTests
{
    [Fact]
    public void Typed_values_move_to_same_named_fields_and_extracted_values_do_not()
    {
        var previous = new List<IndexValue>
        {
            new() { FieldName = "Supplier", Value = "Acme Pty Ltd", IsManual = true },
            new() { FieldName = "Invoice No", Value = "INV-9", IsManual = false },
            new() { FieldName = "Only on old type", Value = "x", IsManual = true }
        };
        var extracted = new List<IndexValue>
        {
            new() { FieldName = "supplier ", Value = "ACME", Confidence = 60 },
            new() { FieldName = "Invoice No", Value = "INV-10", Confidence = 90 },
            new() { FieldName = "Total", Format = FieldFormat.Money, Value = "" }
        };

        ManualValueCarryOver.Apply(extracted, previous, "en-AU");

        Assert.Equal(["Acme Pty Ltd", "INV-10", ""], extracted.Select(value => value.Value));
        Assert.True(extracted[0].IsManual);
        Assert.Equal(100, extracted[0].Confidence);
        Assert.False(extracted[1].IsManual);
    }

    [Fact]
    public void A_typed_value_is_revalidated_for_the_new_field_format()
    {
        var previous = new List<IndexValue> { new() { FieldName = "Date", Value = "not a date", IsManual = true } };
        var extracted = new List<IndexValue> { new() { FieldName = "Date", Format = FieldFormat.Date } };

        ManualValueCarryOver.Apply(extracted, previous, "en-AU");

        Assert.Equal("not a date", extracted[0].Value);
        Assert.NotNull(extracted[0].ValidationError);
    }
}
