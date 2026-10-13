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
        Assert.Equal("http://localhost:11434/v1/chat/completions", OpenAiEndpoints.CompletionsUrl("http://localhost:11434/v1/"));
    }

    [Theory]
    [InlineData("https://res.openai.azure.com", "https://res.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://res.openai.azure.com/openai/v1/", "https://res.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://res.services.ai.azure.com/api/projects/proj", "https://res.services.ai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://res.services.ai.azure.com/models", "https://res.services.ai.azure.com/models/chat/completions?api-version=2024-05-01-preview")]
    [InlineData("https://res.openai.azure.com/openai/deployments/gpt-4o", "https://res.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21")]
    [InlineData("https://res.cognitiveservices.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2025-01-01-preview",
        "https://res.cognitiveservices.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2025-01-01-preview")]
    [InlineData("https://llama.eastus2.models.ai.azure.com", "https://llama.eastus2.models.ai.azure.com/chat/completions")]
    public void Azure_and_foundry_addresses_reach_chat_completions(string endpoint, string expected)
    {
        Assert.Equal(expected, OpenAiEndpoints.CompletionsUrl(endpoint));
    }

    [Fact]
    public void Azure_resources_get_an_api_key_header_and_others_a_bearer_key()
    {
        using var azure = new HttpRequestMessage(HttpMethod.Post, "https://x");
        OpenAiEndpoints.Authorize(azure, "https://res.services.ai.azure.com", "secret");
        Assert.Equal(["secret"], azure.Headers.GetValues("api-key"));
        Assert.Null(azure.Headers.Authorization);

        using var openAi = new HttpRequestMessage(HttpMethod.Post, "https://x");
        OpenAiEndpoints.Authorize(openAi, "https://api.openai.com/v1", "secret");
        Assert.Equal("Bearer", openAi.Headers.Authorization?.Scheme);
        Assert.False(openAi.Headers.Contains("api-key"));
    }
}
