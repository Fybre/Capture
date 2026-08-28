using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class ProfileApplicatorTests
{
    [Fact]
    public void Applies_zonal_and_key_value_fields()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words =
            [
                Word("Invoice", 0.10f, 0.10f),
                Word("No", 0.22f, 0.10f),
                Word("00001521", 0.40f, 0.10f),
                Word("Total", 0.10f, 0.50f)
            ]
        };

        var profile = new IndexingProfile
        {
            AutoReadyThreshold = 80,
            Fields =
            [
                new IndexField
                {
                    Name = "InvoiceNo",
                    Kind = FieldKind.KeyValue,
                    KeyPattern = @"Invoice\s*No",
                    ValuePattern = @"\d+",
                    Mandatory = true,
                    PageScope = PageScope.First
                },
                new IndexField
                {
                    Name = "TotalLabel",
                    Kind = FieldKind.Zonal,
                    PageNumber = 1,
                    Zone = new ZoneRect { PageNumber = 1, X = 0.05f, Y = 0.45f, Width = 0.2f, Height = 0.1f }
                }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, [lattice]);
        Assert.Equal(2, values.Count);
        Assert.Equal("00001521", values[0].Value);
        Assert.Equal("Total", values[1].Value);
        Assert.Equal(IndexLevel.Document, values[0].Level);
        Assert.Equal(DocumentStatus.Ready, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void BatchSeparatorValue_field_mirrors_the_ambient_batch_trigger_value()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "BatchBarcode", Kind = FieldKind.BatchSeparatorValue }]
        };

        var withValue = new ProfileApplicator().Apply(profile, [], batchSeparatorValue: "BATCH-42");
        var value = Assert.Single(withValue);
        Assert.Equal("BATCH-42", value.Value);
        Assert.Equal(100, value.Confidence);

        var withoutValue = new ProfileApplicator().Apply(profile, [], batchSeparatorValue: null);
        Assert.Equal(string.Empty, Assert.Single(withoutValue).Value);
    }

    [Fact]
    public void Barcode_field_with_number_scope_only_reads_its_configured_page()
    {
        var field = new IndexField { Kind = FieldKind.Barcode, PageScope = PageScope.Number, PageNumber = 2 };
        var profile = new IndexingProfile { Fields = [field] };
        var pages = Pages(("a.png", "1"), ("b.png", "2"));
        var decoder = new MapDecoder { ["a.png"] = "WRONG", ["b.png"] = "RIGHT" };

        var value = Assert.Single(new ProfileApplicator(decoder).Apply(profile, [], pages: pages));

        Assert.Equal("RIGHT", value.Value);
        Assert.Equal(2, value.PageNumber);
    }

    [Fact]
    public void Barcode_field_with_first_scope_always_reads_page_one()
    {
        var field = new IndexField { Kind = FieldKind.Barcode, PageScope = PageScope.First, PageNumber = 3 };
        var profile = new IndexingProfile { Fields = [field] };
        var pages = Pages(("a.png", "1"), ("b.png", "2"));
        var decoder = new MapDecoder { ["a.png"] = "PAGE-ONE", ["b.png"] = "PAGE-TWO" };

        var value = Assert.Single(new ProfileApplicator(decoder).Apply(profile, [], pages: pages));

        Assert.Equal("PAGE-ONE", value.Value);
    }

    [Fact]
    public void Barcode_field_with_any_scope_scans_every_page_for_a_match()
    {
        var field = new IndexField { Kind = FieldKind.Barcode, PageScope = PageScope.Any };
        var profile = new IndexingProfile { Fields = [field] };
        var pages = Pages(("a.png", "1"), ("b.png", "2"), ("c.png", "3"));
        var decoder = new MapDecoder { ["c.png"] = "FOUND-ON-THREE" };

        var value = Assert.Single(new ProfileApplicator(decoder).Apply(profile, [], pages: pages));

        Assert.Equal("FOUND-ON-THREE", value.Value);
        Assert.Equal(3, value.PageNumber);
    }

    private static List<DocumentPage> Pages(params (string ImagePath, string Number)[] pages) =>
        pages.Select(item => new DocumentPage
        {
            DocumentId = Guid.NewGuid(),
            PageNumber = int.Parse(item.Number),
            SourcePageNumber = int.Parse(item.Number),
            ImagePath = item.ImagePath
        }).ToList();

    private sealed class MapDecoder : Dictionary<string, string>, IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            TryGetValue(imagePath, out var text) ? new BarcodeReadResult(text, "CODE_128", 99) : null;
    }

    [Fact]
    public void Mandatory_missing_needs_review()
    {
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField
                {
                    Name = "InvoiceNo",
                    Kind = FieldKind.KeyValue,
                    KeyPattern = "MissingKey",
                    ValuePattern = @"\d+",
                    Mandatory = true,
                    PageScope = PageScope.First
                }
            ]
        };

        var lattice = new PageLattice { PageNumber = 1, Words = [Word("Hello", 0.1f, 0.1f)] };
        var values = new ProfileApplicator().Apply(profile, [lattice]);
        Assert.True(values[0].IsMissing);
        Assert.Equal(DocumentStatus.NeedsReview, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void Hidden_separator_does_not_block_ready()
    {
        var values = new[]
        {
            new IndexValue { FieldName = "Sep", Mandatory = true, HideFromIndexing = true },
            new IndexValue { FieldName = "Invoice", Value = "1", Confidence = 99 }
        };
        Assert.Equal(DocumentStatus.Ready, IndexFormat.StatusFor(values, 80));
    }

    private static LatticeWord Word(string text, float x, float y) => new()
    {
        Text = text,
        Confidence = 95,
        X = x,
        Y = y,
        Width = 0.10f,
        Height = 0.04f
    };
}

public class IndexFormatTests
{
    [Fact]
    public void Validates_integer_and_money()
    {
        Assert.Null(IndexFormat.Validate("12", FieldFormat.Integer, "en-AU"));
        Assert.NotNull(IndexFormat.Validate("12.3", FieldFormat.Integer, "en-AU"));
        Assert.Null(IndexFormat.Validate("1,234.50", FieldFormat.Money, "en-AU"));
        Assert.Null(IndexFormat.Validate("yes", FieldFormat.Boolean, null));
        Assert.NotNull(IndexFormat.Validate("maybe", FieldFormat.Boolean, null));
    }
}
