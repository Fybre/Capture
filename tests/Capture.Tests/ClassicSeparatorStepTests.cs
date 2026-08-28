using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class ClassicSeparatorStepTests
{
    [Fact]
    public async Task Produces_the_same_splits_as_calling_PageSeparator_directly()
    {
        var field = new IndexField { Kind = FieldKind.Barcode };
        var profile = new IndexingProfile
        {
            Fields = [field],
            Separation = new DocumentSeparation { Trigger = DocumentSeparationTrigger.Barcode, BarcodeFieldId = field.Id }
        };
        var pages = Pages("a.png", "b.png", "c.png");
        var decoder = new MapDecoder
        {
            ["a.png"] = "DOC-1",
            ["c.png"] = "DOC-2"
        };

        var expected = PageSeparator.Split(pages, profile, decoder, blanks: null);
        var step = new ClassicSeparatorStep(decoder, blanks: null);

        var actual = await step.RunAsync(new PreIndexContext
        {
            Pages = pages,
            SourcePath = "source.pdf",
            CandidateProfiles = [profile]
        });

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].SourcePages, actual[i].SourcePages);
            Assert.Equal(expected[i].SeparatorValues, actual[i].SeparatorValues);
            Assert.Same(profile, actual[i].Profile);
        }
    }

    [Fact]
    public async Task With_no_candidate_profile_returns_all_pages_as_one_unassigned_split()
    {
        var pages = Pages("a.png", "b.png");
        var step = new ClassicSeparatorStep();

        var actual = await step.RunAsync(new PreIndexContext
        {
            Pages = pages,
            SourcePath = "source.pdf",
            CandidateProfiles = []
        });

        var split = Assert.Single(actual);
        Assert.Null(split.Profile);
        Assert.Equal([1, 2], split.SourcePages);
    }

    private static IReadOnlyList<RasterPage> Pages(params string[] names) =>
        names.Select((name, index) => new RasterPage(index + 1, name, 10, 10, 96)).ToList();

    private sealed class MapDecoder : Dictionary<string, string>, IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            TryGetValue(imagePath, out var text) ? new BarcodeReadResult(text, "CODE_128", 99) : null;
    }
}
