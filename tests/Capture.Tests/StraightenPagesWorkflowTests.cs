using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Pdf;
using SkiaSharp;

namespace Capture.Tests;

public sealed class StraightenPagesWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "capture-straighten-flow-" + Guid.NewGuid().ToString("N"));

    public StraightenPagesWorkflowTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Lines of "text" in the top part of the page only, so which way up it is can be told from the image.
    private string Page(float turnClockwise)
    {
        var path = Path.Combine(_root, "scan.png");
        using var bitmap = new SKBitmap(1240, 1754);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(620, 877);
            canvas.RotateDegrees(turnClockwise);
            canvas.Translate(-620, -877);
            using var ink = new SKPaint { Color = SKColors.Black };
            var random = new Random(3);
            for (var line = 0; line < 16; line++)
            {
                for (var x = 120f; x < 1080;)
                {
                    var word = random.Next(20, 90);
                    canvas.DrawRect(x, 150 + line * 36, word, 14, ink);
                    x += word + random.Next(10, 20);
                }
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static bool InkIsInTopHalf(string imagePath)
    {
        using var bitmap = SKBitmap.Decode(imagePath);
        long top = 0, bottom = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
            for (var x = 0; x < bitmap.Width; x += 4)
                if (bitmap.GetPixel(x, y).Red < 128)
                {
                    if (y < bitmap.Height / 2) top++;
                    else bottom++;
                }
        return top > bottom * 2;
    }

    private static readonly LatticeWord[] Readable = Enumerable.Range(0, 12)
        .Select(index => new LatticeWord { Text = index % 2 == 0 ? "Invoice" : "Total", Confidence = 95 }).ToArray();

    private (CaptureWorkflowService Workflow, RecordingLatticeBuilder Builder) Workflow(Func<string, IReadOnlyList<LatticeWord>> words) =>
        Workflow(new RecordingLatticeBuilder(words));

    private (CaptureWorkflowService, RecordingLatticeBuilder) Workflow(RecordingLatticeBuilder builder)
    {
        var workflow = new CaptureWorkflowService(
            new AppPaths(Path.Combine(_root, "app")),
            pdfs: null!,
            new CopyingImageImporter(),
            builder,
            new NoBarcodes(),
            new NeverBlank(),
            new CapturePlanner(),
            materializer: null!,
            straightener: new SkiaPageStraightener(),
            rotator: new SkiaImageRotator(),
            ocr: new ReadsWhenInkIsAtTheTop());
        return (workflow, builder);
    }

    [Fact]
    public async Task A_tilted_scan_is_straightened_and_read_again()
    {
        var (workflow, builder) = Workflow(_ => Readable);

        await workflow.PreviewAsync(new CaptureProfile(), [Page(5)], DocumentSource.Scan);

        Assert.Equal(2, builder.Reads.Count);
        Assert.InRange(builder.Reads[0].Skew, 4.7, 5.3);
        Assert.InRange(builder.Reads[1].Skew, -0.3, 0.3);
    }

    [Fact]
    public async Task An_upside_down_scan_that_reads_poorly_is_turned_the_right_way_up()
    {
        var (workflow, builder) = Workflow(path => InkIsInTopHalf(path) ? Readable : []);

        await workflow.PreviewAsync(new CaptureProfile(), [Page(180)], DocumentSource.Scan);

        Assert.False(builder.Reads[0].InkAtTop);
        Assert.True(builder.Reads[^1].InkAtTop);
    }

    [Fact]
    public async Task An_upright_level_scan_is_left_alone()
    {
        var (workflow, builder) = Workflow(_ => Readable);

        await workflow.PreviewAsync(new CaptureProfile(), [Page(0)], DocumentSource.Scan);

        Assert.Single(builder.Reads);
    }

    [Fact]
    public async Task Nothing_is_corrected_when_the_profile_turns_it_off()
    {
        var (workflow, builder) = Workflow(path => InkIsInTopHalf(path) ? Readable : []);

        await workflow.PreviewAsync(new CaptureProfile { StraightenPages = false }, [Page(180)], DocumentSource.Scan);

        Assert.Single(builder.Reads);
    }

    private sealed class RecordingLatticeBuilder(Func<string, IReadOnlyList<LatticeWord>> words) : ILatticeBuilder
    {
        public List<(double Skew, bool InkAtTop)> Reads { get; } = [];

        public Task<PageLattice> BuildPageAsync(CaptureDocument document, DocumentPage page, CancellationToken cancellationToken = default, OcrOptions? ocr = null)
        {
            lock (Reads)
                Reads.Add((new SkiaPageStraightener().DetectSkew(page.ImagePath), InkIsInTopHalf(page.ImagePath)));
            return Task.FromResult(new PageLattice
            {
                PageNumber = page.PageNumber, PixelWidth = page.Width, PixelHeight = page.Height, Dpi = page.Dpi,
                Source = LatticeSource.Ocr, Words = words(page.ImagePath)
            });
        }

        public Task BuildDocumentAsync(CaptureDocument document, IReadOnlyList<DocumentPage> pages, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ReadsWhenInkIsAtTheTop : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult
            {
                Words = InkIsInTopHalf(imagePath)
                    ? Readable.Select(word => new OcrWord { Text = word.Text, Confidence = word.Confidence, Width = 10, Height = 10 }).ToList()
                    : []
            });
    }

    private sealed class CopyingImageImporter : IImagePageImporter
    {
        public Task<IReadOnlyList<RasterPage>> ImportAsync(string imagePath, string outputDirectory, CancellationToken cancellationToken = default, int? dpiOverride = null)
        {
            var copy = Path.Combine(outputDirectory, "0001.png");
            File.Copy(imagePath, copy, overwrite: true);
            return Task.FromResult<IReadOnlyList<RasterPage>>([new RasterPage(1, copy, 1240, 1754, 150)]);
        }
    }

    private sealed class NoBarcodes : IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) => null;
    }

    private sealed class NeverBlank : IBlankPageDetector
    {
        public bool IsBlank(string imagePath, float maxInkPercent) => false;
    }
}
