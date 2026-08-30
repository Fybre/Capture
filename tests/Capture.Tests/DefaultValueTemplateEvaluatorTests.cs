using Capture.Core.Indexing;

namespace Capture.Tests;

public class DefaultValueTemplateEvaluatorTests
{
    private static DefaultValueContext Context(
        int documentNumber = 1,
        int batchNumber = 1,
        DateTimeOffset? timestamp = null,
        string? profileName = null,
        IReadOnlyDictionary<string, string>? fields = null) => new()
    {
        DocumentNumber = documentNumber,
        BatchNumber = batchNumber,
        Timestamp = timestamp ?? new DateTimeOffset(2026, 9, 15, 8, 30, 45, TimeSpan.Zero),
        ProfileName = profileName,
        Fields = fields ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    };

    [Fact]
    public void Plain_text_passes_through_unchanged()
    {
        Assert.Equal("just some text", DefaultValueTemplateEvaluator.Evaluate("just some text", Context()));
    }

    [Fact]
    public void Doc_hash_defaults_to_no_padding_and_pipe_param_zero_pads()
    {
        var context = Context(documentNumber: 7);
        Assert.Equal("7", DefaultValueTemplateEvaluator.Evaluate("{Doc#}", context));
        Assert.Equal("007", DefaultValueTemplateEvaluator.Evaluate("{Doc#|000}", context));
    }

    [Fact]
    public void Batch_hash_defaults_to_no_padding_and_pipe_param_zero_pads()
    {
        var context = Context(batchNumber: 42);
        Assert.Equal("42", DefaultValueTemplateEvaluator.Evaluate("{Batch#}", context));
        Assert.Equal("0042", DefaultValueTemplateEvaluator.Evaluate("{Batch#|0000}", context));
    }

    [Fact]
    public void Date_and_time_support_custom_dotnet_format_strings_containing_colons()
    {
        var context = Context();
        Assert.Equal("2026-09-15", DefaultValueTemplateEvaluator.Evaluate("{Date}", context));
        Assert.Equal("15.09.2026", DefaultValueTemplateEvaluator.Evaluate("{Date|dd.MM.yyyy}", context));
        Assert.Equal("08:30:45", DefaultValueTemplateEvaluator.Evaluate("{Time}", context));
        Assert.Equal("08-30-45", DefaultValueTemplateEvaluator.Evaluate("{Time|HH-mm-ss}", context));
    }

    [Fact]
    public void Profile_name_token_resolves_from_context()
    {
        Assert.Equal("Invoice", DefaultValueTemplateEvaluator.Evaluate("{ProfileName}", Context(profileName: "Invoice")));
        Assert.Equal(string.Empty, DefaultValueTemplateEvaluator.Evaluate("{ProfileName}", Context(profileName: null)));
    }

    [Fact]
    public void Unknown_token_resolves_as_a_field_reference_case_insensitively()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["InvoiceNo"] = "INV-1" };
        Assert.Equal("INV-1", DefaultValueTemplateEvaluator.Evaluate("{InvoiceNo}", Context(fields: fields)));
        Assert.Equal("INV-1", DefaultValueTemplateEvaluator.Evaluate("{invoiceno}", Context(fields: fields)));
    }

    [Fact]
    public void Missing_field_reference_resolves_to_empty()
    {
        Assert.Equal(string.Empty, DefaultValueTemplateEvaluator.Evaluate("{DoesNotExist}", Context()));
    }

    [Fact]
    public void Composite_template_matches_the_documents_own_example()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "This is the name" };
        var context = Context(documentNumber: 1, fields: fields, timestamp: new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            "1-15.09.2026-This is the name",
            DefaultValueTemplateEvaluator.Evaluate("{Doc#}-{Date|dd.MM.yyyy}-{Name}", context));
    }

    [Fact]
    public void Double_braces_produce_a_literal_brace()
    {
        Assert.Equal("{literal}", DefaultValueTemplateEvaluator.Evaluate("{{literal}}", Context()));
    }

    [Fact]
    public void Unterminated_brace_is_passed_through_literally_rather_than_throwing()
    {
        Assert.Equal("prefix {Doc#", DefaultValueTemplateEvaluator.Evaluate("prefix {Doc#", Context()));
    }

    [Fact]
    public void Null_or_empty_template_resolves_to_empty()
    {
        Assert.Equal(string.Empty, DefaultValueTemplateEvaluator.Evaluate(null, Context()));
        Assert.Equal(string.Empty, DefaultValueTemplateEvaluator.Evaluate(string.Empty, Context()));
    }
}
