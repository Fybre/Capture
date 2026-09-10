using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class CapturePlannerTests
{
    [Fact]
    public void Student_records_plan_preserves_consumed_header_values_and_types()
    {
        var (profile, studentRule) = StudentProfile();
        var plan = new CapturePlanner().Plan(profile,
        [
            Input("one", P("one", 1, "STUDENT:1001", "STUDENT:1001"), P("one", 2, "ENROLMENT FORM"), P("one", 3, "continuation"), P("one", 4, "ACADEMIC TRANSCRIPT")),
            Input("two", P("two", 1, "STUDENT:1002", "STUDENT:1002"), P("two", 2, "ACADEMIC TRANSCRIPT"))
        ]);

        Assert.Equal(2, plan.Batches.Count);
        var firstStudentNumber = plan.Batches[0].CapturedValues.Single(value => value.FieldName == "StudentNumber");
        Assert.Equal("1001", firstStudentNumber.Value);
        Assert.Equal(100, firstStudentNumber.Confidence);
        Assert.Equal(["Enrolment Form", "Transcript"], plan.Batches[0].Documents.Select(document => document.Type!.Name));
        Assert.Equal([2, 3], plan.Batches[0].Documents[0].SourcePages.Select(page => page.PageNumber));
        Assert.Equal(2, plan.ConsumedPages.Count);
        Assert.Contains(plan.Batches[0].CapturedValues, value => value.FieldId != studentRule);
    }

    [Fact]
    public void Profile_with_no_document_types_plans_untyped_documents_into_a_generic_batch()
    {
        // This is the shape of the built-in "Unsorted" profile (BuiltInCaptureProfiles.Unsorted) —
        // no document types and no batch-start rules means every page falls through to the
        // no-type-matched fallback, and the batch is generic so it can join an already-open batch.
        var profile = Profile(batchRule: null);

        var plan = new CapturePlanner().Plan(profile, [Input("one", P("one", 1, "anything"))]);

        var batch = Assert.Single(plan.Batches);
        Assert.True(batch.IsGeneric);
        var document = Assert.Single(batch.Documents);
        Assert.Null(document.Type);
        Assert.Equal([1], document.SourcePages.Select(page => page.PageNumber));
    }

    [Fact]
    public void Batch_plan_remembers_the_start_page_even_when_it_is_consumed()
    {
        var (profile, _) = StudentProfile();

        var plan = new CapturePlanner().Plan(profile,
        [
            Input("one", P("one", 1, "header", "STUDENT:1001"), P("one", 2, "ENROLMENT FORM"))
        ]);

        var batch = Assert.Single(plan.Batches);
        Assert.Equal(new SourcePage("one", 1), batch.BoundaryPage);
        Assert.DoesNotContain(batch.BoundaryPage!, Assert.Single(batch.Documents).SourcePages);
    }

    [Fact]
    public void Batch_separator_value_preserves_barcode_decoder_confidence()
    {
        var (profile, _) = StudentProfile();
        var rule = Assert.Single(profile.Batch.StartRules.Rules);
        var separator = new AnalyzedPage(
            "one",
            1,
            string.Empty,
            [new AnalyzedBarcode("STUDENT:1001", "QR_CODE", 87, rule.Id)]);

        var plan = new CapturePlanner().Plan(profile,
        [
            Input("one", separator, P("one", 2, "ENROLMENT FORM"))
        ]);

        var value = Assert.Single(plan.Batches).CapturedValues.Single();
        Assert.Equal("1001", value.Value);
        Assert.Equal(87, value.Confidence);
    }

    [Fact]
    public void Regex_separator_capture_uses_percentage_confidence()
    {
        var rule = Rule(SeparationStrategyType.Regex, "^BATCH:([0-9]+)$");
        var type = Type("Letter", Rule(SeparationStrategyType.Regex, "LETTER"));
        var field = new IndexField { Name = "Batch", Kind = FieldKind.BatchSeparatorValue, BoundaryRuleId = rule.Id };
        var profile = Profile(rule, type);
        profile.Batch.Fields = [field];

        var plan = new CapturePlanner().Plan(profile,
        [
            Input("one", P("one", 1, "BATCH:42"), P("one", 2, "LETTER"))
        ]);

        var value = Assert.Single(plan.Batches).CapturedValues.Single();
        Assert.Equal("42", value.Value);
        Assert.Equal(100, value.Confidence);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void Identification_can_optionally_start_each_new_document(bool identificationStartsDocument, int expectedDocuments)
    {
        var identify = Rule(SeparationStrategyType.Regex, "^INVOICE$");
        var type = new DocumentTypeDefinition
        {
            Name = "Invoice",
            IdentificationStartsNewDocument = identificationStartsDocument,
            RecognitionRules = new RuleSet { Rules = [identify] }
        };
        var profile = Profile(null, type);
        profile.FileIsDocumentBoundary = false;

        var plan = new CapturePlanner().Plan(profile,
        [
            Input("one",
                P("one", 1, "INVOICE"),
                P("one", 2, "continuation"),
                P("one", 3, "INVOICE"),
                P("one", 4, "continuation"))
        ]);

        var documents = Assert.Single(plan.Batches).Documents;
        Assert.Equal(expectedDocuments, documents.Count);
        Assert.All(documents, document => Assert.Equal("Invoice", document.Type!.Name));
        Assert.Equal(4, documents.Sum(document => document.SourcePages.Count));
    }

    [Fact]
    public void Content_before_header_enters_real_generic_batch()
    {
        var (profile, _) = StudentProfile();
        var plan = new CapturePlanner().Plan(profile, [Input("one", P("one", 1, "loose"), P("one", 2, "STUDENT:1", "STUDENT:1"), P("one", 3, "ENROLMENT FORM"))]);
        Assert.True(plan.Batches[0].IsGeneric);
        Assert.Equal(1, plan.Batches[0].Documents[0].SourcePages[0].PageNumber);
        Assert.False(plan.Batches[1].IsGeneric);
    }

    [Fact]
    public void Same_page_can_start_batch_and_document()
    {
        var batchRule = Rule(SeparationStrategyType.Regex, "HEADER INVOICE");
        var docRule = Rule(SeparationStrategyType.Regex, "INVOICE");
        var profile = Profile(batchRule, Type("Invoice", docRule));
        var plan = new CapturePlanner().Plan(profile, [Input("one", P("one", 1, "HEADER INVOICE"))]);
        Assert.Single(plan.Batches);
        Assert.Single(plan.Batches[0].Documents);
        Assert.Equal("Invoice", plan.Batches[0].Documents[0].Type!.Name);
    }

    [Fact]
    public void Repeated_first_page_matches_create_repeated_instances_and_type_can_change()
    {
        var invoice = Type("Invoice", Rule(SeparationStrategyType.Regex, "^INVOICE"));
        var statement = Type("Statement", Rule(SeparationStrategyType.Regex, "^STATEMENT"));
        var profile = Profile(null, invoice, statement);
        var plan = new CapturePlanner().Plan(profile, [Input("one", P("one", 1, "INVOICE A"), P("one", 2, "body"), P("one", 3, "INVOICE B"), P("one", 4, "STATEMENT"))]);
        Assert.Equal(["Invoice", "Invoice", "Statement"], plan.Batches.Single().Documents.Select(document => document.Type!.Name));
    }

    [Fact]
    public void Open_strategy_batch_continues_across_inputs()
    {
        var (profile, _) = StudentProfile();
        var plan = new CapturePlanner().Plan(profile,
        [
            Input("one", P("one", 1, "STUDENT:1", "STUDENT:1"), P("one", 2, "ENROLMENT FORM")),
            Input("two", P("two", 1, "continuation"))
        ]);
        Assert.Single(plan.Batches);
        Assert.Equal(2, plan.Batches[0].Documents.Count);
        Assert.Equal("two", plan.Batches[0].Documents[1].SourcePages.Single().InputId);
    }

    [Fact]
    public void Ambiguous_types_produce_diagnostic()
    {
        var profile = Profile(null, Type("A", Rule(SeparationStrategyType.Regex, "FORM")), Type("B", Rule(SeparationStrategyType.Regex, "FORM")));
        var plan = new CapturePlanner().Plan(profile, [Input("one", P("one", 1, "FORM"))]);
        Assert.Contains(plan.Diagnostics, item => item.Code == "ambiguous-document-type");
    }

    [Fact]
    public void File_boundary_is_configurable_independently_of_batch_grouping()
    {
        var type = Type("Letter", Rule(SeparationStrategyType.Regex, "LETTER"));
        var profile = Profile(null, type);
        profile.FileIsDocumentBoundary = false;
        var plan = new CapturePlanner().Plan(profile, [Input("one", P("one", 1, "LETTER")), Input("two", P("two", 1, "continued"))]);
        var document = Assert.Single(Assert.Single(plan.Batches).Documents);
        Assert.Equal(["one", "two"], document.SourcePages.Select(page => page.InputId));
    }

    [Fact]
    public void New_batch_for_each_file_creates_a_deterministic_file_boundary()
    {
        var type = Type("Letter", Rule(SeparationStrategyType.Regex, "LETTER"));
        var profile = Profile(null, type);
        profile.Batch.StartNewBatchForEachFile = true;

        var plan = new CapturePlanner().Plan(profile,
        [
            Input("one", P("one", 1, "LETTER one")),
            Input("two", P("two", 1, "LETTER two"))
        ]);

        Assert.Equal(2, plan.Batches.Count);
        Assert.Equal("one", plan.Batches[0].Documents.Single().SourcePages.Single().InputId);
        Assert.Equal("two", plan.Batches[1].Documents.Single().SourcePages.Single().InputId);
    }

    [Fact]
    public void None_batch_start_mode_ignores_retained_rules()
    {
        var profile = Profile(
            Rule(SeparationStrategyType.Regex, "HEADER"),
            Type("Letter", Rule(SeparationStrategyType.Regex, ".")));
        profile.Batch.StartRules.MatchMode = SeparationMatchMode.None;
        profile.Batch.TriggerPageDisposition = PageDisposition.Consume;

        var plan = new CapturePlanner().Plan(profile,
            [Input("one", P("one", 1, "HEADER one"), P("one", 2, "HEADER two"))]);

        Assert.Single(plan.Batches);
        Assert.True(plan.Batches[0].IsGeneric);
        Assert.Empty(plan.ConsumedPages);
    }

    [Fact]
    public void Zone_contents_rule_without_regex_matches_non_empty_zone_text()
    {
        var rule = new SeparationStrategy { Type = SeparationStrategyType.OcrZone };
        var type = Type("Invoice", rule);
        var profile = Profile(null, type);
        var page = new AnalyzedPage("one", 1, "unrelated page text", [], false,
            new Dictionary<Guid, string> { [rule.Id] = "INVOICE" });

        var plan = new CapturePlanner().Plan(profile, [Input("one", page)]);

        Assert.Equal("Invoice", Assert.Single(Assert.Single(plan.Batches).Documents).Type!.Name);
    }

    private static (CaptureProfile Profile, Guid RuleId) StudentProfile()
    {
        var batchRule = Rule(SeparationStrategyType.Barcode, "^STUDENT:([0-9]+)$");
        var profile = Profile(batchRule,
            Type("Enrolment Form", Rule(SeparationStrategyType.Regex, "ENROLMENT FORM")),
            Type("Transcript", Rule(SeparationStrategyType.Regex, "ACADEMIC TRANSCRIPT")));
        profile.Batch.TriggerPageDisposition = PageDisposition.Consume;
        profile.Batch.Fields = [new IndexField { Name = "StudentNumber", BoundaryRuleId = batchRule.Id }];
        return (profile, batchRule.Id);
    }

    private static CaptureProfile Profile(SeparationStrategy? batchRule, params DocumentTypeDefinition[] types) => new()
    {
        Batch = new BatchDefinition { StartRules = new RuleSet { Rules = batchRule is null ? [] : [batchRule] } },
        DocumentTypes = [.. types]
    };
    private static DocumentTypeDefinition Type(string name, SeparationStrategy rule) => new() { Name = name, StartRules = new RuleSet { Rules = [rule] } };
    private static SeparationStrategy Rule(SeparationStrategyType type, string pattern) => new() { Type = type, TextPattern = type == SeparationStrategyType.Regex ? pattern : null, BarcodeValuePattern = type == SeparationStrategyType.Barcode ? pattern : null };
    private static AnalyzedInput Input(string id, params AnalyzedPage[] pages) => new(id, pages);
    private static AnalyzedPage P(string input, int page, string text, string? barcode = null) => new(input, page, text, barcode is null ? [] : [new AnalyzedBarcode(barcode)]);
}
