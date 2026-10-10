using System.Text.Json;
using Capture.App.ViewModels;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class FieldDescriptionTests
{
    [Fact]
    public void Description_survives_a_json_round_trip()
    {
        var field = new IndexField { Name = "PO Number", Description = "Top right of the invoice" };

        var copy = JsonSerializer.Deserialize<IndexField>(JsonSerializer.Serialize(field))!;

        Assert.Equal("Top right of the invoice", copy.Description);
    }

    [Fact]
    public void Designer_writes_a_trimmed_description_and_clears_a_blank_one()
    {
        var row = new FieldRow(new IndexField { Name = "PO Number" });

        row.Description = "  Top right of the invoice \n";
        Assert.Equal("Top right of the invoice", row.Field.Description);

        row.Description = "   ";
        Assert.Null(row.Field.Description);
    }

    [Fact]
    public void Review_row_shows_the_info_tip_only_when_there_is_a_description()
    {
        var value = new IndexValue { FieldName = "PO Number" };

        Assert.False(new IndexValueRow(value, 80, null).HasDescription);
        Assert.True(new IndexValueRow(value, 80, null) { Description = "Top right of the invoice" }.HasDescription);
    }
}
