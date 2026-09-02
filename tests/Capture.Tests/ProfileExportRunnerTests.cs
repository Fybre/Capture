using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Scripting;
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

    private sealed class CapturingWriter : IExportWriter
    {
        public ExportType Type => ExportType.Csv;
        public string? SeenValue { get; private set; }

        public Task<ExportResult> ExportAsync(
            ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default)
        {
            SeenValue = context.IndexValues.SingleOrDefault()?.Value;
            return Task.FromResult(new ExportResult(true, null));
        }
    }

    private sealed class FakeScriptRunner : IFieldScriptRunner
    {
        public bool IsAvailable { get; set; } = true;
        public Func<FieldScript, ScriptExecutionContext, ScriptRunResult>? OnProfileScript { get; set; }

        public Task<ScriptRunResult> RunProfileScriptAsync(FieldScript script, ScriptExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnProfileScript?.Invoke(script, context) ?? ScriptRunResult.Ok(null, TimeSpan.Zero));

        public Task<ScriptRunResult> RunFieldExpressionAsync(Guid scriptCacheKey, string expression, ScriptExecutionContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Export triggers only run profile-level scripts.");
    }

    [Fact]
    public async Task BeforeExport_script_reshapes_what_a_writer_sees_without_touching_the_callers_list()
    {
        var writer = new CapturingWriter();
        var runner = new ProfileExportRunner([writer], new FakeScriptRunner
        {
            OnProfileScript = (_, ctx) =>
            {
                ctx.Values.Single().Value = "REWRITTEN";
                return ScriptRunResult.Ok(null, TimeSpan.Zero);
            }
        });
        var fieldId = Guid.NewGuid();
        var profile = new IndexingProfile
        {
            Exports = [new ExportDefinition { Enabled = true, Type = ExportType.Csv }],
            Scripts = [new FieldScript { Trigger = ScriptTrigger.BeforeExport, Source = "..." }]
        };
        var original = new IndexValue { FieldId = fieldId, FieldName = "Total", Value = "original" };

        await runner.RunAsync(profile, MakeDocument(), [original]);

        Assert.Equal("REWRITTEN", writer.SeenValue);
        Assert.Equal("original", original.Value); // caller's own list is never mutated
    }

    [Fact]
    public async Task AfterExport_script_runs_once_writers_have_finished()
    {
        var writer = new FakeExportWriter();
        var callOrder = new List<string>();
        var runner = new ProfileExportRunner([writer], new FakeScriptRunner
        {
            OnProfileScript = (script, _) =>
            {
                callOrder.Add(script.Name);
                return ScriptRunResult.Ok(null, TimeSpan.Zero);
            }
        });
        var profile = new IndexingProfile
        {
            Exports = [new ExportDefinition { Enabled = true, Type = ExportType.Csv }],
            Scripts =
            [
                new FieldScript { Name = "Before", Trigger = ScriptTrigger.BeforeExport, Source = "..." },
                new FieldScript { Name = "After", Trigger = ScriptTrigger.AfterExport, Source = "..." }
            ]
        };

        var results = await runner.RunAsync(profile, MakeDocument(), []);

        Assert.True(results.Single().Success);
        Assert.Equal(["Before", "After"], callOrder);
    }

    [Fact]
    public async Task Export_scripts_are_skipped_when_the_runner_is_unavailable()
    {
        var writer = new FakeExportWriter();
        var runner = new ProfileExportRunner([writer], new FakeScriptRunner
        {
            IsAvailable = false,
            OnProfileScript = (_, _) => throw new InvalidOperationException("should not run when unavailable")
        });
        var profile = new IndexingProfile
        {
            Exports = [new ExportDefinition { Enabled = true, Type = ExportType.Csv }],
            Scripts = [new FieldScript { Trigger = ScriptTrigger.BeforeExport, Source = "..." }]
        };

        var results = await runner.RunAsync(profile, MakeDocument(), []);

        Assert.True(results.Single().Success);
    }
}
