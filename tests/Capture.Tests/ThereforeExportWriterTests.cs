using System.Text.Json;
using Capture.Core.Profiles;
using Capture.Export;
using Capture.Therefore;

namespace Capture.Tests;

public class ThereforeExportWriterTests
{
    private static ThereforeFieldMapping Mapping(ThereforeFieldType type) => new()
    {
        FieldNo = 42,
        Caption = "Test Field",
        IndexDataFieldName = "Test_Field",
        FieldType = (int)type,
        Mandatory = false
    };

    private static JsonElement Serialize(object item)
    {
        var json = JsonSerializer.Serialize(item);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Int_field_with_a_parseable_value_produces_IntIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Int), "123"));

        var data = element.GetProperty("IntIndexData");
        Assert.Equal("Test_Field", data.GetProperty("FieldName").GetString());
        Assert.Equal(123, data.GetProperty("DataValue").GetInt64());
    }

    [Fact]
    public void Int_field_with_an_unparseable_value_falls_back_to_string()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Int), "not-a-number"));

        var data = element.GetProperty("StringIndexData");
        Assert.Equal("not-a-number", data.GetProperty("DataValue").GetString());
    }

    [Fact]
    public void Money_field_with_a_parseable_value_produces_MoneyIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Money), "19.99"));

        var data = element.GetProperty("MoneyIndexData");
        Assert.Equal(19.99m, data.GetProperty("DataValue").GetDecimal());
    }

    [Fact]
    public void Money_field_with_an_unparseable_value_falls_back_to_string()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Money), "lots"));

        Assert.Equal("lots", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Fact]
    public void Date_field_with_a_parseable_value_produces_DateIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Date), "2026-01-15"));

        Assert.True(element.TryGetProperty("DateIndexData", out _));
    }

    [Fact]
    public void Date_field_with_an_unparseable_value_falls_back_to_string()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Date), "whenever"));

        Assert.Equal("whenever", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("Yes", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    public void Logical_field_parses_truthy_values(string value, bool expected)
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Logical), value));

        Assert.Equal(expected, element.GetProperty("LogicalIndexData").GetProperty("DataValue").GetBoolean());
    }

    [Fact]
    public void String_field_always_produces_StringIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.String), "hello"));

        Assert.Equal("hello", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Theory]
    [InlineData(ThereforeFieldType.Table)]
    [InlineData(ThereforeFieldType.Custom)]
    public void Table_and_custom_fields_fall_back_to_string(ThereforeFieldType type)
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(type), "raw-value"));

        Assert.Equal("raw-value", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Fact]
    public void Falls_back_to_caption_when_IndexDataFieldName_is_empty()
    {
        var mapping = new ThereforeFieldMapping
        {
            FieldNo = 1, Caption = "Fallback Caption", IndexDataFieldName = "", FieldType = (int)ThereforeFieldType.String
        };

        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(mapping, "value"));

        Assert.Equal("Fallback Caption", element.GetProperty("StringIndexData").GetProperty("FieldName").GetString());
    }
}
