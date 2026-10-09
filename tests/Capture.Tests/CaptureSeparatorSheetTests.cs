using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Profiles;
using Capture.Pdf;

namespace Capture.Tests;

public class CaptureSeparatorSheetTests
{
    [Theory]
    [InlineData(CaptureSeparatorKind.Document)]
    [InlineData(CaptureSeparatorKind.Batch)]
    public async Task A_generated_sheet_decodes_back_to_its_own_separator_value(CaptureSeparatorKind kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-sep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pdfPath = Path.Combine(root, "sheet.pdf");
            await using (var stream = File.Create(pdfPath))
                CaptureSeparatorSheetWriter.Write(stream, kind);

            // Same rasterize-then-decode path a scanned or imported page goes through.
            var pages = await new PdfiumRasterizer().RasterizeAsync(pdfPath, Path.Combine(root, "pages"), 200);
            var page = Assert.Single(pages);
            var decoded = new ZxingBarcodeDecoder().Decode(page.ImagePath, null);

            Assert.NotNull(decoded);
            Assert.True(CaptureSeparatorSheet.Matches(kind, decoded!.Text), $"decoded '{decoded.Text}'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Batch_and_document_sheets_start_only_their_own_boundary()
    {
        var batchRule = Separator(CaptureSeparatorKind.Batch);
        var documentRule = Separator(CaptureSeparatorKind.Document);
        var profile = new CaptureProfile
        {
            Batch = new BatchDefinition { StartRules = new RuleSet { Rules = [batchRule] }, TriggerPageDisposition = PageDisposition.Consume },
            DocumentTypes =
            [
                new DocumentTypeDefinition
                {
                    Name = "Letter",
                    StartRules = new RuleSet { Rules = [documentRule] },
                    TriggerPageDisposition = PageDisposition.Consume
                }
            ]
        };
        profile.DefaultDocumentTypeId = profile.DocumentTypes[0].Id;

        var plan = new CapturePlanner().Plan(profile,
        [
            new AnalyzedInput("scan",
            [
                Sheet(1, CaptureSeparatorSheet.BatchValue, batchRule, documentRule),
                Page(2),
                Sheet(3, CaptureSeparatorSheet.DocumentValue, batchRule, documentRule),
                Page(4),
                Sheet(5, CaptureSeparatorSheet.BatchValue, batchRule, documentRule),
                Page(6)
            ])
        ]);

        Assert.Equal(2, plan.Batches.Count);
        Assert.Equal([[2], [4]], plan.Batches[0].Documents.Select(document => document.SourcePages.Select(page => page.PageNumber).ToArray()));
        Assert.Equal([6], Assert.Single(plan.Batches[1].Documents).SourcePages.Select(page => page.PageNumber));
        Assert.Equal([1, 3, 5], plan.ConsumedPages.Select(page => page.PageNumber));
    }

    [Fact]
    public void An_ordinary_barcode_rule_does_not_fire_on_a_separator_sheet()
    {
        var anyBarcode = new SeparationStrategy { Type = SeparationStrategyType.Barcode };
        var sheetRule = Separator(CaptureSeparatorKind.Document);
        var profile = new CaptureProfile
        {
            Batch = new BatchDefinition { StartRules = new RuleSet { Rules = [anyBarcode] } },
            DocumentTypes = [new DocumentTypeDefinition { Name = "Letter", StartRules = new RuleSet { Rules = [sheetRule] } }]
        };

        // How CaptureWorkflowService tags the whole-page separator decode: per separator rule only.
        var plan = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("scan", [Sheet(1, CaptureSeparatorSheet.DocumentValue, sheetRule), Page(2)])]);

        Assert.True(Assert.Single(plan.Batches).IsGeneric);
    }

    private static SeparationStrategy Separator(CaptureSeparatorKind kind) =>
        new() { Type = SeparationStrategyType.CaptureSeparator, SeparatorKind = kind };

    private static AnalyzedPage Sheet(int pageNumber, string value, params SeparationStrategy[] sheetRules) =>
        new("scan", pageNumber, string.Empty, sheetRules.Select(rule => new AnalyzedBarcode(value, "QR_CODE", 95, rule.Id)).ToList());

    private static AnalyzedPage Page(int pageNumber) => new("scan", pageNumber, "content", []);
}
