using System.Text.Json;
using Capture.App.Services;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;

namespace Capture.Tests;

public class ExternalOcrTests
{
    private static readonly OcrResult GoodPrint = Result(("Invoice", 95), ("number", 94), ("INV-1001", 92), ("Total", 96), ("amount", 93), ("due", 95));
    private static readonly OcrResult PoorHandwriting = Result(("Iuv", 31), ("~ol", 22), ("rn", 40), ("Tqtal", 35), ("dve", 28), ("amnt", 30));
    private static readonly OcrResult AzureRead = Result(("Invoice", 99), ("number", 98), ("INV-1001", 97), ("Total", 99), ("amount", 98), ("due", 99));

    private static OcrResult Result(params (string Text, float Confidence)[] words) => new()
    {
        Width = 1000,
        Height = 1000,
        Words = words.Select((word, index) => new OcrWord { Text = word.Text, Confidence = word.Confidence, X = index * 100, Y = 10, Width = 90, Height = 20 }).ToList()
    };

    private static (LatticeBuilder Builder, FakeExternal External) Builder(OcrResult builtIn, Exception? externalFailure = null)
    {
        var external = new FakeExternal(AzureRead, externalFailure);
        var builder = new LatticeBuilder(
            new AppPaths(Path.Combine(Path.GetTempPath(), "capture-external-ocr-" + Guid.NewGuid().ToString("N"))),
            new NoopLatticeStore(), new NoPdfText(), new FixedOcr(builtIn), new NoRasterizer(), external);
        return (builder, external);
    }

    private static readonly CaptureDocument Scan = new() { OriginalFileName = "scan.png", StoredPath = "scan.png" };
    private static readonly DocumentPage Page = new() { DocumentId = Scan.Id, PageNumber = 1, SourcePageNumber = 1, ImagePath = "scan.png", Width = 1000, Height = 1000, Dpi = 300 };

    [Fact]
    public async Task Built_in_only_never_calls_the_external_service()
    {
        var (builder, external) = Builder(PoorHandwriting);

        var lattice = await builder.BuildPageAsync(Scan, Page, ocr: OcrOptions.BuiltInOnly);

        Assert.Equal(LatticeSource.Ocr, lattice.Source);
        Assert.Equal(0, external.Calls);
    }

    [Fact]
    public async Task All_pages_mode_uses_the_external_service_for_every_scanned_page()
    {
        var (builder, external) = Builder(GoodPrint);

        var lattice = await builder.BuildPageAsync(Scan, Page, ocr: new OcrOptions(ExternalOcrMode.AllPages));

        Assert.Equal(LatticeSource.ExternalOcr, lattice.Source);
        Assert.Equal(1, external.Calls);
        Assert.Equal(99, lattice.Words[0].Confidence);
    }

    [Fact]
    public async Task When_poor_mode_keeps_good_built_in_ocr_and_sends_poor_pages()
    {
        var (good, goodExternal) = Builder(GoodPrint);
        var (poor, poorExternal) = Builder(PoorHandwriting);
        var options = new OcrOptions(ExternalOcrMode.WhenPoor, MinConfidence: 70);

        var kept = await good.BuildPageAsync(Scan, Page, ocr: options);
        var replaced = await poor.BuildPageAsync(Scan, Page, ocr: options);

        Assert.Equal(LatticeSource.Ocr, kept.Source);
        Assert.Equal(0, goodExternal.Calls);
        Assert.Equal(LatticeSource.ExternalOcr, replaced.Source);
        Assert.Equal(1, poorExternal.Calls);
        Assert.Null(replaced.Warning);
    }

    [Fact]
    public async Task A_failed_external_call_falls_back_to_built_in_ocr_with_a_warning()
    {
        var (allPages, _) = Builder(GoodPrint, new InvalidOperationException("Azure refused the key (401)"));
        var (whenPoor, _) = Builder(PoorHandwriting, new InvalidOperationException("Azure returned 429"));

        var all = await allPages.BuildPageAsync(Scan, Page, ocr: new OcrOptions(ExternalOcrMode.AllPages));
        var poor = await whenPoor.BuildPageAsync(Scan, Page, ocr: new OcrOptions(ExternalOcrMode.WhenPoor));

        Assert.Equal(LatticeSource.Ocr, all.Source);
        Assert.Equal("Invoice", all.Words[0].Text);
        Assert.Contains("Azure refused the key (401)", all.Warning);
        Assert.Equal(LatticeSource.Ocr, poor.Source);
        Assert.Contains("read this page poorly", poor.Warning);
    }

    [Fact]
    public void A_page_with_no_words_is_taken_as_blank_not_poor()
    {
        Assert.False(LatticeQuality.IsPoorOcr([], 70));
    }

    [Fact]
    public void Reads_azure_words_into_ocr_boxes()
    {
        using var json = JsonDocument.Parse("""
            {"status":"succeeded","analyzeResult":{"pages":[{"pageNumber":1,"width":2480,"height":3508,"unit":"pixel",
              "words":[{"content":"Hello","polygon":[100,200,300,205,298,260,98,255],"confidence":0.987},
                       {"content":"","polygon":[1,1,2,1,2,2,1,2],"confidence":0.5}]}]}}
            """);

        var result = AzureDocumentIntelligenceOcr.Read(json.RootElement);

        Assert.Equal(2480, result.Width);
        Assert.Equal(3508, result.Height);
        var word = Assert.Single(result.Words);
        Assert.Equal("Hello", word.Text);
        Assert.Equal(98.7f, word.Confidence, 1);
        Assert.Equal(98, word.X);
        Assert.Equal(200, word.Y);
        Assert.Equal(202, word.Width);
        Assert.Equal(60, word.Height);
    }

    [Fact]
    public void Azure_inch_positions_keep_their_precision()
    {
        using var json = JsonDocument.Parse("""
            {"analyzeResult":{"pages":[{"width":8.5,"height":11,"unit":"inch",
              "words":[{"content":"A","polygon":[1,1,2,1,2,1.5,1,1.5],"confidence":0.9}]}]}}
            """);

        var result = AzureDocumentIntelligenceOcr.Read(json.RootElement);

        Assert.Equal(8500, result.Width);
        Assert.Equal(1000, Assert.Single(result.Words).X);
    }

    [Fact]
    public void An_azure_connection_is_set_up_with_an_endpoint_and_key_alone()
    {
        var connection = new Capture.Core.Watch.ConnectionDefinition
        {
            Type = Capture.Core.Watch.ConnectionType.AzureDocumentIntelligence,
            BaseUrl = "https://example.cognitiveservices.azure.com/",
            AuthMethod = Capture.Core.Watch.ConnectionAuthMethod.ApiKey,
            Token = "key",
            ApiKeyHeader = null
        };

        Assert.True(connection.IsConfigured);
    }

    private sealed class FakeExternal(OcrResult result, Exception? failure) : IExternalOcrEngine
    {
        public int Calls { get; private set; }

        public Task<OcrResult> RecognizeAsync(Guid? connectionId, string imagePath, CancellationToken cancellationToken = default)
        {
            Calls++;
            return failure is null ? Task.FromResult(result) : Task.FromException<OcrResult>(failure);
        }
    }

    private sealed class FixedOcr(OcrResult result) : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class NoPdfText : IPdfTextExtractor
    {
        public Task<IReadOnlyList<LatticeWord>?> TryExtractPageAsync(string pdfPath, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LatticeWord>?>(null);
    }

    private sealed class NoRasterizer : IPdfRasterizer
    {
        public Task<IReadOnlyList<RasterPage>> RasterizeAsync(string pdfPath, string outputDirectory, int dpi, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RasterizePageAsync(string pdfPath, int pageNumber, string outputPath, int dpi, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopLatticeStore : ILatticeStore
    {
        public Task SaveAsync(Guid documentId, PageLattice lattice, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PageLattice?> GetAsync(Guid documentId, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<PageLattice?>(null);
    }
}
