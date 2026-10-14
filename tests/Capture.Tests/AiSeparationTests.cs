using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class AiSeparationPlannerTests
{
    private static readonly DocumentTypeDefinition Invoice = new() { Name = "Invoice", AiDescription = "A bill." };
    private static readonly DocumentTypeDefinition Statement = new() { Name = "Statement", AiDescription = "An account statement." };

    private static CaptureProfile Profile(params DocumentTypeDefinition[] types) => new()
    {
        AiSeparation = true,
        DocumentTypes = [.. types],
        DefaultDocumentTypeId = types.FirstOrDefault()?.Id
    };

    private static AnalyzedPage Page(int number, AiPageVerdict? ai, string text = "", string? barcode = null) =>
        new("one", number, text, barcode is null ? [] : [new AnalyzedBarcode(barcode)], Ai: ai);

    private static AiPageVerdict New(string type, int confidence = 95) => new(true, type, confidence, "New header.");
    private static AiPageVerdict Same(int confidence = 95) => new(false, null, confidence, "Page 2 of 2.");

    [Fact]
    public void Ai_starts_and_types_documents()
    {
        var profile = Profile(Invoice, Statement);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [Page(1, New("invoice")), Page(2, Same()), Page(3, New("invoice")), Page(4, New("statement"))])]);

        var documents = plan.Batches.SelectMany(batch => batch.Documents).ToList();
        Assert.Equal(["Invoice", "Invoice", "Statement"], documents.Select(document => document.Type?.Name));
        Assert.Equal([2, 1, 1], documents.Select(document => document.SourcePages.Count));
        Assert.All(documents, document => Assert.Null(document.ReviewReasons));
    }

    [Fact]
    public void A_rule_start_and_type_override_the_ai()
    {
        var receipt = new DocumentTypeDefinition
        {
            Name = "Receipt",
            StartRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "RECEIPT" }] }
        };
        var profile = Profile(Invoice, receipt);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [Page(1, New("invoice")), Page(2, Same(), "RECEIPT 42")])]);

        var documents = plan.Batches.SelectMany(batch => batch.Documents).ToList();
        Assert.Equal(["Invoice", "Receipt"], documents.Select(document => document.Type?.Name));
    }

    [Fact]
    public void A_type_set_to_let_the_ai_decide_ignores_its_rules_but_keeps_their_values()
    {
        var rule = new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "INVOICE" };
        var invoice = new DocumentTypeDefinition
        {
            Name = "Invoice",
            AiIgnoresRules = true,
            StartRules = new RuleSet { Rules = [rule] }
        };
        var profile = Profile(Statement, invoice);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [Page(1, New("statement"), "INVOICE 1"), Page(2, Same(), "INVOICE 2"), Page(3, New("invoice"), "INVOICE 3")])]);

        var documents = plan.Batches.SelectMany(batch => batch.Documents).ToList();
        Assert.Equal(["Statement", "Invoice"], documents.Select(document => document.Type?.Name));
        Assert.Equal([2, 1], documents.Select(document => document.SourcePages.Count));
        Assert.Equal([rule.Id], documents[1].BoundaryMatches.Select(match => match.RuleId));
    }

    [Fact]
    public void A_type_set_to_let_the_ai_decide_still_uses_its_rules_without_ai_verdicts()
    {
        var invoice = new DocumentTypeDefinition
        {
            Name = "Invoice",
            AiIgnoresRules = true,
            StartRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "INVOICE" }] }
        };
        var profile = Profile(invoice);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [Page(1, null, "INVOICE 1"), Page(2, null, "INVOICE 2")])]);

        Assert.Equal(2, plan.Batches.SelectMany(batch => batch.Documents).Count());
    }

    [Fact]
    public void A_document_no_type_fits_keeps_the_ais_own_label()
    {
        var profile = Profile(Invoice);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [
                Page(1, New("invoice")),
                Page(2, new AiPageVerdict(true, null, 90, "Bank logo.", "Bank Statement")),
                Page(3, Same())])]);

        var documents = plan.Batches.SelectMany(batch => batch.Documents).ToList();
        Assert.Equal([null, "Bank Statement"], documents.Select(document => document.AiSuggestedType));
        Assert.Equal([1, 2], documents.Select(document => document.SourcePages.Count));
        Assert.Null(documents[0].ReviewReasons);
        Assert.Equal(
            ["None of this profile's document types fit — the AI calls it \"Bank Statement\", so it was given the fallback type, Invoice. Check the document type."],
            documents[1].ReviewReasons);
    }

    [Fact]
    public void Test_capture_lists_the_labels_of_documents_no_type_fits()
    {
        var profile = Profile(Invoice);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [
                Page(1, new AiPageVerdict(true, null, 90, "", "Timesheet")),
                Page(2, new AiPageVerdict(true, null, 90, "", "Bank Statement")),
                Page(3, new AiPageVerdict(true, null, 90, "", "bank statement"))])]);

        var summary = Capture.App.ViewModels.CaptureProfileDesignerViewModel.SuggestedTypesSummary(plan);
        Assert.Contains("Bank Statement ×2, Timesheet.", summary);
        Assert.Equal(string.Empty, Capture.App.ViewModels.CaptureProfileDesignerViewModel.SuggestedTypesSummary(
            new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [Page(1, New("invoice"))])])));
    }

    [Fact]
    public void A_separator_sheet_is_still_consumed()
    {
        var profile = Profile(Invoice);
        profile.DocumentTypes[0] = new DocumentTypeDefinition
        {
            Name = "Invoice",
            StartRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.CaptureSeparator, SeparatorKind = CaptureSeparatorKind.Document }] },
            TriggerPageDisposition = PageDisposition.Consume
        };
        var rule = profile.DocumentTypes[0].StartRules.Rules[0];
        var plan = new CapturePlanner().Plan(profile,
        [
            new AnalyzedInput("one",
            [
                Page(1, New("invoice")),
                new AnalyzedPage("one", 2, "", [new AnalyzedBarcode(CaptureSeparatorSheet.DocumentValue, RuleId: rule.Id)], Ai: Same()),
                Page(3, Same())
            ])
        ]);

        Assert.Single(plan.ConsumedPages);
        Assert.Equal(2, plan.Batches.SelectMany(batch => batch.Documents).Count());
    }

    [Fact]
    public void A_type_that_does_not_start_documents_joins_the_one_before()
    {
        var attachment = new DocumentTypeDefinition { Name = "Delivery Docket", AiStartsNewDocument = false };
        var profile = Profile(Invoice, attachment);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [Page(1, New("invoice")), Page(2, New("delivery-docket"))])]);

        var document = Assert.Single(plan.Batches.SelectMany(batch => batch.Documents));
        Assert.Equal("Invoice", document.Type?.Name);
        Assert.Equal(2, document.SourcePages.Count);
    }

    [Fact]
    public void A_low_confidence_decision_is_applied_and_flagged()
    {
        var profile = Profile(Invoice, Statement);
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("one", [Page(1, New("invoice")), Page(2, New("statement", 55))])]);

        var documents = plan.Batches.SelectMany(batch => batch.Documents).ToList();
        Assert.Equal(2, documents.Count);
        Assert.Null(documents[0].ReviewReasons);
        var reason = Assert.Single(documents[1].ReviewReasons!);
        Assert.Contains("55%", reason);
        Assert.Contains("page 2 starts a new Statement", reason);
    }

    [Fact]
    public void An_unknown_type_falls_back_to_the_default_type()
    {
        var profile = Profile(Invoice, Statement);
        var plan = new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [Page(1, New("other"))])]);

        Assert.Equal("Invoice", Assert.Single(plan.Batches.SelectMany(batch => batch.Documents)).Type?.Name);
    }

    [Fact]
    public void Without_ai_verdicts_documents_are_split_by_rules_and_flagged()
    {
        var profile = Profile(Invoice);
        var plan = new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [Page(1, null), Page(2, null)])]);

        var document = Assert.Single(plan.Batches.SelectMany(batch => batch.Documents));
        Assert.Equal([CapturePlanner.AiNotSetUpReason], document.ReviewReasons);
    }

    [Fact]
    public void Ai_verdicts_are_ignored_when_ai_separation_is_off()
    {
        var profile = Profile(Invoice, Statement);
        profile.AiSeparation = false;
        var plan = new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [Page(1, New("invoice")), Page(2, New("statement"))])]);

        var document = Assert.Single(plan.Batches.SelectMany(batch => batch.Documents));
        Assert.Null(document.ReviewReasons);
    }

    [Fact]
    public void Ai_type_ids_are_readable_slugs_of_the_type_names()
    {
        var profile = Profile(new DocumentTypeDefinition { Name = "Credit Note / Adjustment", AiDescription = " Reverses an invoice. " });

        var type = Assert.Single(CapturePlanner.AiTypes(profile)!);
        Assert.Equal("credit-note-adjustment", type.Id);
        Assert.Equal("Reverses an invoice.", type.Description);
    }

    [Fact]
    public void A_fallback_type_without_a_description_is_not_offered_to_the_ai()
    {
        var fallback = new DocumentTypeDefinition { Name = "Default" };
        var profile = Profile(fallback, Invoice);
        Assert.Equal(["invoice"], CapturePlanner.AiTypes(profile)!.Select(type => type.Id));

        fallback.AiDescription = "Anything that isn't an invoice.";
        Assert.Equal(["default", "invoice"], CapturePlanner.AiTypes(profile)!.Select(type => type.Id));
    }
}

public class AiSeparationPromptTests
{
    private static readonly IReadOnlyList<AiSeparationType> Types =
        [new("invoice", "Invoice", "A bill."), new("statement", "Statement", "An account statement.")];

    [Fact]
    public void Parses_a_fenced_answer_and_matches_the_type()
    {
        var verdict = AiSeparationPrompt.Parse(
            "```json\n{\"is_new_document\": true, \"document_type\": \"invoice\", \"confidence\": 88, \"reasoning\": \"Different invoice number.\"}\n```",
            Types);

        Assert.Equal(new AiPageVerdict(true, "invoice", 88, "Different invoice number."), verdict);
    }

    [Theory]
    [InlineData("{\"is_new_document\": false, \"document_type\": \"Statement\", \"confidence\": 0.85}", "statement", 85)]
    [InlineData("{\"is_new_document\": false, \"document_type\": \"other\", \"confidence\": \"40\"}", null, 40)]
    [InlineData("{\"is_new_document\": false}", null, 0)]
    public void Reads_names_fractions_and_missing_values(string json, string? type, int confidence)
    {
        var verdict = AiSeparationPrompt.Parse(json, Types);

        Assert.Equal(type, verdict.TypeId);
        Assert.Equal(confidence, verdict.Confidence);
    }

    [Theory]
    [InlineData("{\"is_new_document\":true,\"document_type\":\"other\",\"suggested_type\":\" Bank  Statement \",\"confidence\":90}", "Bank Statement")]
    [InlineData("{\"is_new_document\":true,\"document_type\":\"Timesheet\",\"confidence\":90}", "Timesheet")]
    [InlineData("{\"is_new_document\":true,\"document_type\":\"other\",\"suggested_type\":\"\",\"confidence\":90}", null)]
    [InlineData("{\"is_new_document\":true,\"document_type\":\"invoice\",\"suggested_type\":\"Tax Invoice\",\"confidence\":90}", null)]
    public void A_label_is_kept_only_when_no_type_fits(string json, string? suggested)
    {
        Assert.Equal(suggested, AiSeparationPrompt.Parse(json, Types).SuggestedType);
    }

    [Fact]
    public void An_answer_without_json_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => AiSeparationPrompt.Parse("I think it's an invoice.", Types));
    }

    [Fact]
    public void The_system_message_keeps_the_docsplitter_rules_and_lists_the_types()
    {
        var system = AiSeparationPrompt.SystemMessage(Types);

        Assert.Contains("extracted text is authoritative", system);
        Assert.Contains("same template ≠ continuation", system);
        Assert.Contains("remittance slip on an invoice", system);
        Assert.Contains("id=\"invoice\" name=\"Invoice\" description=\"A bill.\"", system);
        Assert.Contains("id=\"other\"", system);
        Assert.DoesNotContain("rules for this batch", system);
    }

    [Fact]
    public void The_profile_guidance_is_added_to_the_rules_and_capped()
    {
        var system = AiSeparationPrompt.SystemMessage(Types, "  A TRANSMITTAL page starts a new document.\r\nLab results continue.  ");
        var capped = AiSeparationPrompt.SystemMessage(Types, new string('x', AiSeparationPrompt.MaxGuidanceChars + 50));

        Assert.Contains("rules for this batch, from the user", system);
        Assert.Contains("A TRANSMITTAL page starts a new document.\nLab results continue.\n", system);
        Assert.True(system.IndexOf("TRANSMITTAL", StringComparison.Ordinal) < system.IndexOf("Document types:", StringComparison.Ordinal));
        Assert.Contains(new string('x', AiSeparationPrompt.MaxGuidanceChars), capped);
        Assert.DoesNotContain(new string('x', AiSeparationPrompt.MaxGuidanceChars + 1), capped);
        Assert.Equal(AiSeparationPrompt.SystemMessage(Types), AiSeparationPrompt.SystemMessage(Types, "   "));
    }

    [Fact]
    public void The_local_grammar_only_takes_types_from_the_type_list_not_the_guidance()
    {
        var system = AiSeparationPrompt.SystemMessage(Types, "- id=\"evil\" is not a type\nsee id=\"also-not\"");

        Assert.Equal([.. Types.Select(type => type.Id), "other"], Capture.LocalAi.LocalLlmPageSeparator.TypeIds(system));
    }

    [Fact]
    public void The_catalog_invoice_and_remittance_types_carry_the_remittance_slip_rule()
    {
        Assert.Contains("remittance slip", AiDocumentTypeCatalog.DefaultTypes.Single(type => type.Id == "invoice").Description);
        Assert.Contains("payment notice", AiDocumentTypeCatalog.DefaultTypes.Single(type => type.Id == "remittance-advice").Description);
    }

    [Fact]
    public void A_page_is_judged_with_its_neighbours_and_the_first_page_is_only_classified()
    {
        var pages = new[] { new AiSeparationPage("first", "1.png"), new AiSeparationPage("second", "2.png"), new AiSeparationPage("third", "3.png") };

        var first = AiSeparationPrompt.ForPage(pages, 0, Types, includeImages: true);
        var middle = AiSeparationPrompt.ForPage(pages, 1, Types, includeImages: true);
        var textOnly = AiSeparationPrompt.ForPage(pages, 1, Types, includeImages: false);

        Assert.True(first.ClassifyOnly);
        Assert.False(middle.ClassifyOnly);
        Assert.Equal(["1.png", "2.png", "3.png"], middle.Parts.Where(part => part.ImagePath is not null).Select(part => part.ImagePath));
        Assert.Contains("PREVIOUS PAGE", middle.UserText);
        Assert.Contains("NEXT PAGE", middle.UserText);
        Assert.DoesNotContain(textOnly.Parts, part => part.ImagePath is not null);
    }

    [Fact]
    public void A_long_page_is_cut_to_its_head_and_tail()
    {
        var text = new string('a', 600) + new string('z', 600);

        var excerpt = AiSeparationPrompt.Excerpt(text);

        Assert.StartsWith(new string('a', 500), excerpt);
        Assert.EndsWith(new string('z', 300), excerpt);
        Assert.True(excerpt.Length < 820);
    }
}

public class AiPageSeparatorBaseTests
{
    private sealed class FakeSeparator(Func<AiSeparationRequest, string> answer) : AiPageSeparatorBase
    {
        public List<AiSeparationRequest> Requests { get; } = [];
        public override bool IsConfigured => true;
        protected override bool IncludeImages => false;

        protected override Task<string> CompleteAsync(AiSeparationRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static readonly IReadOnlyList<AiSeparationType> Types = [new("invoice", "Invoice", "")];

    [Fact]
    public async Task Blank_pages_skip_the_ai_and_a_failed_page_is_flagged_not_thrown()
    {
        var separator = new FakeSeparator(request => request.UserText.Contains("CURRENT PAGE (classify this one):\nExtracted text:\nbroken")
            ? throw new HttpRequestException("timeout")
            : "{\"is_new_document\": false, \"document_type\": \"invoice\", \"confidence\": 90, \"reasoning\": \"ok\"}");

        var verdicts = await separator.AnalyzeAsync(
            [new AiSeparationPage("first"), new AiSeparationPage("", IsBlank: true), new AiSeparationPage("broken")], Types);

        Assert.Equal(2, separator.Requests.Count);
        Assert.True(verdicts[0].StartsNewDocument);
        Assert.Equal(new AiPageVerdict(false, null, 100, "Blank page."), verdicts[1]);
        Assert.Equal(0, verdicts[2].Confidence);
        Assert.Equal("AI failed: timeout", verdicts[2].Reason);
    }

    [Fact]
    public async Task A_page_that_must_start_a_document_always_does()
    {
        var separator = new FakeSeparator(_ => "{\"is_new_document\": false, \"document_type\": \"invoice\", \"confidence\": 90}");

        var verdicts = await separator.AnalyzeAsync([new AiSeparationPage("a"), new AiSeparationPage("b", ForceNew: true)], Types);

        Assert.All(verdicts, verdict => Assert.True(verdict.StartsNewDocument));
        Assert.All(separator.Requests, request => Assert.True(request.ClassifyOnly));
    }

    [Fact]
    public async Task Progress_counts_every_page_including_ones_the_ai_skips()
    {
        var separator = new FakeSeparator(_ => "{\"is_new_document\": false, \"document_type\": \"invoice\", \"confidence\": 90}");
        var reports = new System.Collections.Concurrent.ConcurrentBag<int>();

        await separator.AnalyzeAsync(
            [new AiSeparationPage("a"), new AiSeparationPage("", IsBlank: true), new AiSeparationPage("c"), new AiSeparationPage("d")],
            Types, progress: new Collect(reports));

        Assert.Equal([1, 2, 3, 4], reports.Order());
    }

    private sealed class Collect(System.Collections.Concurrent.ConcurrentBag<int> reports) : IProgress<int>
    {
        public void Report(int value) => reports.Add(value);
    }

    [Fact]
    public async Task The_guidance_is_sent_with_every_page()
    {
        var separator = new FakeSeparator(_ => "{\"is_new_document\": true, \"document_type\": \"invoice\", \"confidence\": 90}");

        await separator.AnalyzeAsync([new AiSeparationPage("a"), new AiSeparationPage("b")], null, "Cover sheets start documents.");

        Assert.Single(separator.Requests);
        Assert.Contains("Cover sheets start documents.", separator.Requests[0].SystemMessage);
    }
}

public class AiSplitOnlyTests
{
    private static readonly SeparationStrategy InvoiceRule = new() { Type = SeparationStrategyType.Regex, TextPattern = "TAX INVOICE" };
    private static readonly DocumentTypeDefinition Fallback = new() { Name = "Default" };
    private static readonly DocumentTypeDefinition Invoice = new() { Name = "Invoice", RecognitionRules = new RuleSet { Rules = [InvoiceRule] } };
    private static readonly DocumentTypeDefinition Statement = new()
    {
        Name = "Statement",
        RecognitionRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "STATEMENT OF ACCOUNT" }] }
    };

    private static CaptureProfile Profile() => new()
    {
        AiSeparation = true,
        AiClassifies = false,
        DocumentTypes = [Fallback, Invoice, Statement],
        DefaultDocumentTypeId = Fallback.Id
    };

    private static AnalyzedPage Page(int number, bool starts, string text = "", int confidence = 95) =>
        new("one", number, text, [], Ai: new AiPageVerdict(starts, null, confidence, starts ? "New header." : "Continues."));

    private static List<PlannedDocument> Documents(CaptureProfile profile, params AnalyzedPage[] pages) =>
        new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [.. pages])]).Batches.SelectMany(batch => batch.Documents).ToList();

    [Fact]
    public void The_ai_splits_and_each_documents_rules_decide_its_type_from_any_of_its_pages()
    {
        var documents = Documents(Profile(),
            Page(1, true, "Cover sheet"), Page(2, false, "TAX INVOICE 42"),
            Page(3, true, "Something else"));

        Assert.Equal(["Invoice", "Default"], documents.Select(document => document.Type?.Name));
        Assert.Equal([2, 1], documents.Select(document => document.SourcePages.Count));
        Assert.Equal([InvoiceRule.Id], documents[0].BoundaryMatches.Select(match => match.RuleId));
        Assert.All(documents, document => Assert.Null(document.ReviewReasons));
        Assert.All(documents, document => Assert.Null(document.AiSuggestedType));
    }

    [Fact]
    public void Pages_matching_different_types_are_flagged_as_a_possible_missed_split()
    {
        var document = Assert.Single(Documents(Profile(),
            Page(1, true, "TAX INVOICE 42"), Page(2, false, "STATEMENT OF ACCOUNT")));

        Assert.Equal("Invoice", document.Type?.Name);
        var reason = Assert.Single(document.ReviewReasons!);
        Assert.Contains("Invoice on page 1, Statement on page 2", reason);
    }

    [Fact]
    public void A_first_page_rule_still_splits_and_an_unsure_split_is_flagged()
    {
        var profile = Profile();
        profile.DocumentTypes[1] = new DocumentTypeDefinition
        {
            Name = "Invoice",
            StartRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "TAX INVOICE" }] }
        };

        var documents = Documents(profile,
            Page(1, true, "Letter"), Page(2, false, "TAX INVOICE 7"), Page(3, true, "More", confidence: 40));

        Assert.Equal(["Default", "Invoice", "Default"], documents.Select(document => document.Type?.Name));
        Assert.Null(documents[1].ReviewReasons);
        Assert.Contains("AI was 40% sure page 3 starts a new document", Assert.Single(documents[2].ReviewReasons!));
    }

    [Fact]
    public void The_ai_is_shown_no_types()
    {
        Assert.Null(CapturePlanner.AiTypes(Profile()));
    }

    [Fact]
    public async Task Pages_that_start_a_document_anyway_are_not_sent()
    {
        var requests = new List<AiSeparationRequest>();
        var separator = new RecordingSeparator(requests);

        var verdicts = await separator.AnalyzeAsync(
            [new AiSeparationPage("first"), new AiSeparationPage("second"), new AiSeparationPage("third", ForceNew: true)], null);

        var request = Assert.Single(requests);
        Assert.DoesNotContain("id=\"invoice\"", request.SystemMessage);
        Assert.Equal([true, false, true], verdicts.Select(verdict => verdict.StartsNewDocument));
        Assert.All(verdicts, verdict => Assert.Null(verdict.TypeId));
        Assert.All(verdicts, verdict => Assert.Null(verdict.SuggestedType));
    }

    private sealed class RecordingSeparator(List<AiSeparationRequest> requests) : AiPageSeparatorBase
    {
        public override bool IsConfigured => true;
        protected override bool IncludeImages => false;

        protected override Task<string> CompleteAsync(AiSeparationRequest request, CancellationToken cancellationToken)
        {
            lock (requests) requests.Add(request);
            return Task.FromResult("{\"is_new_document\":false,\"document_type\":\"other\",\"suggested_type\":\"Letter\",\"confidence\":90,\"reasoning\":\"same\"}");
        }
    }
}
