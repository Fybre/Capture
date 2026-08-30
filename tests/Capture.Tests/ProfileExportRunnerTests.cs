using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Export;

namespace Capture.Tests;

public class ProfileExportRunnerTests
{
    private sealed class FakeExportWriter : IExportWriter
    {
        public ExportType Type => ExportType.Csv;
        public int CallCount { get; private set; }

        public Task<ExportResult> ExportAsync(
            ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ExportResult(true, null));
        }
    }

    private static CaptureDocument MakeDocument() =>
        new() { OriginalFileName = "doc.pdf", StoredPath = "/tmp/doc.pdf" };

    [Fact]
    public async Task Runs_only_enabled_definitions()
    {
        var writer = new FakeExportWriter();
        var runner = new ProfileExportRunner([writer]);
        var profile = new IndexingProfile
        {
            Exports =
            [
                new ExportDefinition { Enabled = true, Type = ExportType.Csv },
                new ExportDefinition { Enabled = false, Type = ExportType.Csv },
                new ExportDefinition { Enabled = true, Type = ExportType.Csv }
            ]
        };

        var results = await runner.RunAsync(profile, MakeDocument(), []);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, writer.CallCount);
        Assert.All(results, result => Assert.True(result.Success));
    }

    [Fact]
    public async Task A_definition_with_no_registered_writer_fails_without_blocking_others()
    {
        var writer = new FakeExportWriter();
        var runner = new ProfileExportRunner([writer]);
        var profile = new IndexingProfile
        {
            Exports =
            [
                new ExportDefinition { Enabled = true, Type = (ExportType)999 },
                new ExportDefinition { Enabled = true, Type = ExportType.Csv }
            ]
        };

        var results = await runner.RunAsync(profile, MakeDocument(), []);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
        Assert.Equal(1, writer.CallCount);
    }
}
