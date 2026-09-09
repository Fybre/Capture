using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class CaptureFixturePlannerTests
{
    public static IEnumerable<object[]> Fixtures() => CaptureProfileFixtureContractTests.FixtureFiles();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Planner_matches_the_documented_fixture_contract(string path)
    {
        var fixture = CaptureProfileFixtureContractTests.Load(path);
        var profile = BuildProfile(fixture.Id);
        var inputs = fixture.Inputs.Select(input => new AnalyzedInput(input.File,
            input.Pages.Select(page => new AnalyzedPage(input.File, page.Page, page.Text,
                page.Barcode is null ? [] : [new AnalyzedBarcode(page.Barcode)])).ToList())).ToList();

        var plan = new CapturePlanner().Plan(profile, inputs);

        Assert.Equal(fixture.Expected.Batches.Count, plan.Batches.Count);
        for (var batchIndex = 0; batchIndex < plan.Batches.Count; batchIndex++)
        {
            var actualBatch = plan.Batches[batchIndex];
            var expectedBatch = fixture.Expected.Batches[batchIndex];
            Assert.Equal(expectedBatch.Generic, actualBatch.IsGeneric);
            Assert.Equal(expectedBatch.Fields, actualBatch.CapturedValues.ToDictionary(value => value.FieldName, value => value.Value));
            Assert.Equal(expectedBatch.Documents.Count, actualBatch.Documents.Count);
            for (var documentIndex = 0; documentIndex < actualBatch.Documents.Count; documentIndex++)
            {
                var actual = actualBatch.Documents[documentIndex];
                var expected = expectedBatch.Documents[documentIndex];
                Assert.Equal(expected.Type, actual.Type?.Name ?? "Unclassified");
                Assert.Equal(expected.Pages, actual.SourcePages.Select(PageKey));
                Assert.Equal(expected.BoundaryValues.Values.Order(), actual.BoundaryMatches.Where(match => match.CapturedValue is not null).Select(match => match.CapturedValue!).Order());
            }
        }
        Assert.Equal(fixture.Expected.ConsumedPages.Order(), plan.ConsumedPages.Select(PageKey).Order());
    }

    private static CaptureProfile BuildProfile(string id) => id switch
    {
        "student-records" => Student(),
        "legal-matter" => Legal(),
        "mixed-accounts-payable" => AccountsPayable(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null)
    };

    private static CaptureProfile Student()
    {
        var batchRule = Barcode("^STUDENT:([0-9]+)$");
        return new CaptureProfile
        {
            FileIsDocumentBoundary = false,
            Batch = new BatchDefinition { TriggerPageDisposition = PageDisposition.Consume, StartRules = Rules(batchRule), Fields = [new IndexField { Name = "StudentNumber", BoundaryRuleId = batchRule.Id }] },
            DocumentTypes = [Type("Enrolment Form", Regex("^ENROLMENT FORM$")), Type("Transcript", Regex("^ACADEMIC TRANSCRIPT$"))]
        };
    }

    private static CaptureProfile Legal()
    {
        var batchRule = Barcode("^MATTER:([0-9]+)$");
        return new CaptureProfile
        {
            FileIsDocumentBoundary = false,
            Batch = new BatchDefinition { StartRules = Rules(batchRule), Fields = [new IndexField { Name = "MatterNumber", BoundaryRuleId = batchRule.Id }] },
            DocumentTypes = [Type("Correspondence", Regex("^CORRESPONDENCE MATTER")), Type("Witness Statement", Regex("^WITNESS STATEMENT$"))]
        };
    }

    private static CaptureProfile AccountsPayable()
    {
        var invoiceRule = Regex("^INVOICE:([^\\r\\n]+)$");
        return new CaptureProfile
        {
            FileIsDocumentBoundary = false,
            Batch = new BatchDefinition(),
            DocumentTypes =
            [
                new DocumentTypeDefinition { Name = "Invoice", TriggerPageDisposition = PageDisposition.Consume, StartRules = Rules(invoiceRule), Fields = [new IndexField { Name = "InvoiceNumber", BoundaryRuleId = invoiceRule.Id }] },
                Type("Statement", Regex("^ACCOUNT STATEMENT$"))
            ]
        };
    }

    private static string PageKey(SourcePage page) => $"{page.InputId}#{page.PageNumber}";
    private static DocumentTypeDefinition Type(string name, SeparationStrategy rule) => new() { Name = name, StartRules = Rules(rule) };
    private static RuleSet Rules(SeparationStrategy rule) => new() { Rules = [rule] };
    private static SeparationStrategy Regex(string pattern) => new() { Type = SeparationStrategyType.Regex, TextPattern = pattern };
    private static SeparationStrategy Barcode(string pattern) => new() { Type = SeparationStrategyType.Barcode, BarcodeValuePattern = pattern };
}
