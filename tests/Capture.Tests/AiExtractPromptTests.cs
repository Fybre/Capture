using Capture.Core.Indexing;
using Capture.Core.Profiles;

namespace Capture.Tests;

[Collection("AiFieldCatalog")]
public class AiExtractPromptTests
{
    [Fact]
    public void Catalog_has_invoice_and_contract_classifications()
    {
        Assert.Contains("Invoice", AiFieldCatalog.Classifications);
        Assert.Contains("Contract", AiFieldCatalog.Classifications);
        Assert.Contains(AiFieldCatalog.All, item => item.Name == "Invoice No");
        Assert.Contains(AiFieldCatalog.All, item => item.Name == "Contract No");
        Assert.Equal(FieldFormat.Money, AiFieldCatalog.Find("invoice.total")!.Format);
    }

    [Fact]
    public void Parses_values_by_id_and_name()
    {
        var invoice = new IndexField { Name = "Invoice No", Kind = FieldKind.Ai, AiTypeId = "invoice.number" };
        var supplier = new IndexField { Name = "Supplier Name", Kind = FieldKind.Ai, AiTypeId = "invoice.supplier" };
        var json = $$"""
            {
              "values": {
                "{{invoice.Id:N}}": { "value": "INV-9", "confidence": 91 },
                "Supplier Name": "Acme Pty Ltd"
              }
            }
            """;

        var parsed = AiExtractPrompt.Parse(json, [invoice, supplier]);

        Assert.Equal("INV-9", parsed[invoice.Id].Value);
        Assert.Equal(91, parsed[invoice.Id].Confidence);
        Assert.Equal("Acme Pty Ltd", parsed[supplier.Id].Value);
    }

    [Fact]
    public void Catalog_has_a_custom_type_with_no_preset_meaning()
    {
        var custom = AiFieldCatalog.Find(AiFieldCatalog.CustomTypeId);

        Assert.NotNull(custom);
        Assert.Equal("Custom", custom!.Classification);
        Assert.Equal(string.Empty, custom.Hint);
    }

    [Fact]
    public void Custom_field_prompt_uses_the_users_description_as_the_meaning()
    {
        var field = new IndexField
        {
            Name = "Container Number",
            Kind = FieldKind.Ai,
            AiTypeId = AiFieldCatalog.CustomTypeId,
            AiPrompt = "The container number stamped on the shipping label"
        };

        var prompt = AiExtractPrompt.UserMessage("some document text", [field]);

        Assert.Contains("name=\"Container Number\"", prompt);
        Assert.DoesNotContain("meaning=\"\"", prompt);
        Assert.Contains("extra=\"The container number stamped on the shipping label\"", prompt);
    }

    [Fact]
    public void Custom_field_without_a_description_falls_back_to_its_name()
    {
        var field = new IndexField
        {
            Name = "Container Number",
            Kind = FieldKind.Ai,
            AiTypeId = AiFieldCatalog.CustomTypeId
        };

        var prompt = AiExtractPrompt.UserMessage("some document text", [field]);

        Assert.Contains("meaning=\"Container Number\"", prompt);
    }

    [Fact]
    public void Completions_url_normalizes_endpoint()
    {
        Assert.Equal("https://api.openai.com/v1/chat/completions", OpenAiEndpoints.CompletionsUrl("https://api.openai.com/v1"));
        Assert.Equal("https://host/v1/chat/completions", OpenAiEndpoints.CompletionsUrl("https://host/v1/chat/completions"));
        Assert.Equal("https://host/v1/chat/completions", OpenAiEndpoints.CompletionsUrl("https://host"));
    }
}
