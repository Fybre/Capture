using System.Text.Json;
using Capture.Therefore;

namespace Capture.Tests;

public class ThereforeClientTests
{
    [Fact]
    public void BuildBaseUrl_derives_the_online_url_from_tenant_when_no_url_given()
    {
        Assert.Equal(
            "https://craigdemo.thereforeonline.com/theservice/v0001/restun",
            ThereforeClient.BuildBaseUrl("craigdemo", null));
    }

    [Fact]
    public void BuildBaseUrl_adds_a_scheme_when_missing()
    {
        Assert.Equal(
            "https://onprem.example.com/theservice/v0001/restun",
            ThereforeClient.BuildBaseUrl(null, "onprem.example.com"));
    }

    [Theory]
    [InlineData("https://craigdemo.thereforeonline.com")]
    [InlineData("https://craigdemo.thereforeonline.com/theservice")]
    [InlineData("https://craigdemo.thereforeonline.com/theservice/v0001")]
    [InlineData("https://craigdemo.thereforeonline.com/theservice/v0001/restun")]
    [InlineData("https://craigdemo.thereforeonline.com/theservice/v0001/restun/")]
    public void BuildBaseUrl_normalizes_any_level_of_pasted_url_to_the_same_result(string pasted)
    {
        Assert.Equal(
            "https://craigdemo.thereforeonline.com/theservice/v0001/restun",
            ThereforeClient.BuildBaseUrl("craigdemo", pasted));
    }

    [Fact]
    public void ParseTreeItems_maps_item_type_and_recurses_into_child_items()
    {
        using var doc = JsonDocument.Parse("""
            [
              {
                "ItemNo": 9, "ItemType": 1, "Name": "Testing",
                "ChildItems": [
                  { "ItemNo": 56, "ItemType": 2, "Name": "Test Category", "ChildItems": [] }
                ]
              },
              { "ItemNo": 1, "ItemType": 1, "Name": "System", "ChildItems": [] }
            ]
            """);

        var nodes = ThereforeClient.ParseTreeItems(doc.RootElement);

        Assert.Equal(2, nodes.Count);
        var testing = Assert.Single(nodes, node => node.Name == "Testing");
        Assert.Equal(1, testing.ItemType);
        Assert.False(testing.IsCategory);
        var category = Assert.Single(testing.Children);
        Assert.Equal(56, category.ItemNo);
        Assert.True(category.IsCategory);
    }

    [Fact]
    public void ParseTreeItems_sorts_folders_before_categories_then_alphabetically()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "ItemNo": 2, "ItemType": 2, "Name": "Zebra Category", "ChildItems": [] },
              { "ItemNo": 3, "ItemType": 1, "Name": "Alpha Folder", "ChildItems": [] },
              { "ItemNo": 1, "ItemType": 2, "Name": "Apple Category", "ChildItems": [] }
            ]
            """);

        var nodes = ThereforeClient.ParseTreeItems(doc.RootElement);

        Assert.Equal(["Alpha Folder", "Apple Category", "Zebra Category"], nodes.Select(n => n.Name));
    }

    [Fact]
    public void ParseTreeItems_treats_a_literal_null_child_item_as_empty_not_a_crash()
    {
        // A confirmed live-API quirk: ChildItems can come back as the literal one-element [null] array.
        using var doc = JsonDocument.Parse("""
            [ { "ItemNo": 9, "ItemType": 1, "Name": "Testing", "ChildItems": [ null ] } ]
            """);

        var nodes = ThereforeClient.ParseTreeItems(doc.RootElement);

        var node = Assert.Single(nodes);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void ParseCategoryInfo_maps_label_and_real_fields_from_a_live_response_shape()
    {
        // Trimmed from a real GetCategoryInfo response (category "Invoices", craigdemo tenant).
        using var doc = JsonDocument.Parse("""
            {
              "Name": "Invoices",
              "CategoryFields": [
                {
                  "FieldNo": 957, "Caption": "Invoice No", "ColName": "", "FieldType": 4,
                  "IndexDataFieldName": "Label_Invoice_No", "Mandatory": false,
                  "IsSingleKeyword": false, "IsMultipleKeyword": false
                },
                {
                  "FieldNo": 960, "Caption": "Invoice No", "ColName": "Invoice_No", "FieldType": 1,
                  "IndexDataFieldName": "Invoice_No", "Mandatory": false,
                  "IsSingleKeyword": false, "IsMultipleKeyword": false
                },
                {
                  "FieldNo": 962, "Caption": "Total Net", "ColName": "Total_Net", "FieldType": 5,
                  "IndexDataFieldName": "Total_Net", "Mandatory": true,
                  "IsSingleKeyword": false, "IsMultipleKeyword": false
                },
                {
                  "FieldNo": 8629, "Caption": "Invoice Date", "ColName": "Invoice_Date", "FieldType": 3,
                  "IndexDataFieldName": "Invoice_Date", "Mandatory": false,
                  "IsSingleKeyword": false, "IsMultipleKeyword": false
                }
              ]
            }
            """);

        var info = ThereforeClient.ParseCategoryInfo(doc.RootElement, categoryNo: 57);

        Assert.Equal("Invoices", info.Name);
        Assert.Equal(4, info.Fields.Count);

        var label = info.Fields.Single(f => f.FieldType == ThereforeFieldType.Label);
        Assert.Equal("Invoice No", label.Caption);

        var invoiceNo = info.Fields.Single(f => f.IndexDataFieldName == "Invoice_No");
        Assert.Equal(ThereforeFieldType.String, invoiceNo.FieldType);
        Assert.False(invoiceNo.Mandatory);

        var totalNet = info.Fields.Single(f => f.IndexDataFieldName == "Total_Net");
        Assert.Equal(ThereforeFieldType.Money, totalNet.FieldType);
        Assert.True(totalNet.Mandatory);

        var invoiceDate = info.Fields.Single(f => f.IndexDataFieldName == "Invoice_Date");
        Assert.Equal(ThereforeFieldType.Date, invoiceDate.FieldType);
    }

    [Fact]
    public void ParseCategoryInfo_maps_counter_field_type()
    {
        // Trimmed from a real response (category "format test", craigdemo tenant) — a server-generated
        // sequence field.
        using var doc = JsonDocument.Parse("""
            {
              "Name": "format test",
              "CategoryFields": [
                {
                  "FieldNo": 8261, "Caption": "Formatted Test Counter", "ColName": "Formatted_Test_Counter",
                  "FieldType": 9, "IndexDataFieldName": "Formatted_Test_Counter", "Mandatory": false,
                  "IsSingleKeyword": false, "IsMultipleKeyword": false
                }
              ]
            }
            """);

        var info = ThereforeClient.ParseCategoryInfo(doc.RootElement, categoryNo: 252);

        var field = Assert.Single(info.Fields);
        Assert.Equal(ThereforeFieldType.TextCounter, field.FieldType);
    }
}
