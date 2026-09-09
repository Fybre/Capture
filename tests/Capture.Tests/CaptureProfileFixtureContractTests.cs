using System.Text.Json;

namespace Capture.Tests;

public class CaptureProfileFixtureContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IEnumerable<object[]> FixtureFiles() =>
        Directory.EnumerateFiles(FixtureDirectory, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Every_input_page_has_exactly_one_expected_disposition(string path)
    {
        var fixture = Load(path);
        Assert.Equal(1, fixture.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Id));
        Assert.NotEmpty(fixture.Inputs);
        Assert.NotEmpty(fixture.Expected.Batches);

        var inputPages = fixture.Inputs
            .SelectMany(input => input.Pages.Select(page => $"{input.File}#{page.Page}"))
            .ToList();
        Assert.Equal(inputPages.Count, inputPages.Distinct(StringComparer.Ordinal).Count());

        var documentPages = fixture.Expected.Batches
            .SelectMany(batch => batch.Documents)
            .SelectMany(document => document.Pages)
            .ToList();
        var consumedPages = fixture.Expected.ConsumedPages;

        Assert.Empty(documentPages.Intersect(consumedPages, StringComparer.Ordinal));
        Assert.Equal(inputPages.Order(), documentPages.Concat(consumedPages).Order());
        Assert.Equal(documentPages.Count, documentPages.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(consumedPages.Count, consumedPages.Distinct(StringComparer.Ordinal).Count());

        Assert.All(fixture.Expected.Batches, batch =>
        {
            Assert.False(string.IsNullOrWhiteSpace(batch.Key));
            Assert.NotEmpty(batch.Documents);
            Assert.All(batch.Documents, document =>
            {
                Assert.False(string.IsNullOrWhiteSpace(document.Type));
                Assert.NotEmpty(document.Pages);
            });
        });
    }

    [Fact]
    public void Fixtures_cover_every_required_phase_zero_scenario()
    {
        var covered = FixtureFiles()
            .Select(row => Load((string)row[0]))
            .SelectMany(fixture => fixture.Covers)
            .ToHashSet(StringComparer.Ordinal);

        var required = new[]
        {
            "document-value-survives-consumed-page",
            "same-page-batch-and-document-start",
            "repeated-document-type-instances",
            "document-type-change",
            "content-before-first-batch-header",
            "strategy-batch-across-input-files"
        };

        Assert.True(required.All(covered.Contains),
            $"Missing fixture coverage: {string.Join(", ", required.Where(item => !covered.Contains(item)))}");
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "CaptureProfiles");

    internal static CaptureScenarioFixture Load(string path) =>
        JsonSerializer.Deserialize<CaptureScenarioFixture>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Could not deserialize fixture '{path}'.");
}

internal sealed class CaptureScenarioFixture
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public FixtureConfiguration Configuration { get; set; } = new();
    public List<FixtureInput> Inputs { get; set; } = [];
    public FixtureExpectation Expected { get; set; } = new();
    public List<string> Covers { get; set; } = [];
}

internal sealed class FixtureConfiguration
{
    public string BatchStart { get; set; } = "";
    public string BatchTriggerDisposition { get; set; } = "";
    public List<string> DocumentStarts { get; set; } = [];
    public bool FileIsDocumentBoundary { get; set; }
    public bool StartNewBatchForEachFile { get; set; }
}

internal sealed class FixtureInput
{
    public string File { get; set; } = "";
    public List<FixturePage> Pages { get; set; } = [];
}

internal sealed class FixturePage
{
    public int Page { get; set; }
    public string Text { get; set; } = "";
    public string? Barcode { get; set; }
}

internal sealed class FixtureExpectation
{
    public List<FixtureBatch> Batches { get; set; } = [];
    public List<string> ConsumedPages { get; set; } = [];
}

internal sealed class FixtureBatch
{
    public string Key { get; set; } = "";
    public bool Generic { get; set; }
    public Dictionary<string, string> Fields { get; set; } = [];
    public List<FixtureDocument> Documents { get; set; } = [];
}

internal sealed class FixtureDocument
{
    public string Type { get; set; } = "";
    public List<string> Pages { get; set; } = [];
    public Dictionary<string, string> BoundaryValues { get; set; } = [];
}
