using Capture.App.ViewModels;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Export;
using Capture.Therefore;

namespace Capture.Tests;

public class BooleanFieldTests
{
    [Theory]
    [InlineData("Yes", true)]
    [InlineData(" true ", true)]
    [InlineData("1", true)]
    [InlineData("NO", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void Yes_no_text_parses(string text, bool expected)
    {
        Assert.True(IndexFormat.TryParseBoolean(text, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Null(IndexFormat.Validate(text, FieldFormat.Boolean, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData(null)]
    public void Anything_else_is_not_a_yes_no_value(string? text)
    {
        Assert.False(IndexFormat.TryParseBoolean(text, out _));
    }

    [Fact]
    public void A_boolean_field_edits_as_a_checkbox_and_commits_Yes_or_No()
    {
        var value = new IndexValue { FieldName = "Approved", Format = FieldFormat.Boolean, Kind = FieldKind.Text, Mandatory = true };
        var row = new IndexValueRow(value, threshold: 80, locale: null);

        Assert.True(row.IsBooleanEditorVisible);
        Assert.False(row.IsTextEntry);
        Assert.Null(row.SelectedBoolean);
        Assert.Equal("Not set", row.BooleanDisplay);
        Assert.Equal("Missing", row.Flag);

        row.SelectedBoolean = true;
        Assert.Equal("Yes", value.Value);
        Assert.True(value.IsManual);
        Assert.Equal("Yes", row.BooleanDisplay);
        Assert.False(row.HasFlag);

        row.SelectedBoolean = false;
        Assert.Equal("No", value.Value);
        Assert.Null(value.ValidationError);
    }

    [Fact]
    public void An_extracted_boolean_value_shows_as_checked()
    {
        var value = new IndexValue { FieldName = "Paid", Format = FieldFormat.Boolean, Kind = FieldKind.Regex, Value = "TRUE" };
        var row = new IndexValueRow(value, threshold: 80, locale: null);

        Assert.True(row.SelectedBoolean);
        Assert.Equal("TRUE", value.Value); // displaying it doesn't rewrite the stored value
    }

    [Fact]
    public void A_boolean_lookup_field_stays_a_lookup()
    {
        var value = new IndexValue { FieldName = "Flag", Format = FieldFormat.Boolean, Kind = FieldKind.Lookup };
        var row = new IndexValueRow(value, threshold: 80, locale: null);

        Assert.True(row.IsLookupEditorVisible);
        Assert.False(row.IsBooleanEditorVisible);
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("No", false)]
    [InlineData("0", false)]
    public void Therefore_logical_fields_get_the_parsed_value(string text, bool expected)
    {
        var mapping = new ThereforeFieldMapping { FieldNo = 5, IndexDataFieldName = "Approved", FieldType = (int)ThereforeFieldType.Logical };
        var item = ThereforeExportWriter.BuildIndexDataItem(mapping, text);

        var json = System.Text.Json.JsonSerializer.Serialize(item);
        Assert.Contains($"\"DataValue\":{(expected ? "true" : "false")}", json);
    }
}
