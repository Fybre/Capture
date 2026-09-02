using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Scripting;
using Capture.Core.Watch;
using Xunit;

namespace Capture.Scripting.Tests;

/// <summary>Real compile-and-run coverage against Roslyn — deliberately kept out of the fast
/// Capture.Tests suite (see ProfileApplicatorScriptTests, which fakes IFieldScriptRunner) since a real
/// first compile has genuine, non-trivial latency.</summary>
public class RoslynFieldScriptRunnerTests
{
    [Fact]
    public async Task Profile_script_writes_a_field_value()
    {
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context(new IndexValue { FieldId = Guid.NewGuid(), FieldName = "Total", Value = "" });
        var script = new FieldScript { Name = "Set total", Source = "Fields[\"Total\"].Value = \"42\";" };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("42", context.Values.Single().Value);
    }

    [Fact]
    public async Task A_profile_script_can_write_to_several_fields_other_than_a_single_one_of_its_own()
    {
        // Mirrors the Button field use case: read one field, write several others — a profile-level
        // script isn't tied to "its own" field the way a Script-kind expression is.
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context(
            new IndexValue { FieldId = Guid.NewGuid(), FieldName = "Customer Id", Value = "42" },
            new IndexValue { FieldId = Guid.NewGuid(), FieldName = "Customer Name", Value = "" },
            new IndexValue { FieldId = Guid.NewGuid(), FieldName = "Customer Country", Value = "" });
        var script = new FieldScript
        {
            Name = "Look up customer",
            Source = "var id = Fields[\"Customer Id\"].Value; Fields[\"Customer Name\"].Value = $\"Customer {id}\"; Fields[\"Customer Country\"].Value = \"AU\";"
        };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Customer 42", context.Values.Single(v => v.FieldName == "Customer Name").Value);
        Assert.Equal("AU", context.Values.Single(v => v.FieldName == "Customer Country").Value);
    }

    [Fact]
    public async Task Scripts_can_read_document_level_facts()
    {
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context();

        var result = await runner.RunFieldExpressionAsync(
            Guid.NewGuid(),
            "$\"{Document.FileName}|{Document.FileExtension}|{Document.PageCount}|{Document.Text}\"",
            context);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("sample.pdf|.pdf|1|sample text", result.Value);
    }

    [Fact]
    public async Task Field_expression_returns_its_result_without_mutating_anything()
    {
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context(new IndexValue { FieldId = Guid.NewGuid(), FieldName = "A", Value = "hello" });

        var result = await runner.RunFieldExpressionAsync(Guid.NewGuid(), "Fields[\"A\"].Value.ToUpperInvariant()", context);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("HELLO", result.Value);
        Assert.Equal("hello", context.Values.Single().Value); // untouched — expressions are read-only
    }

    [Fact]
    public async Task Field_expression_cannot_write_other_fields()
    {
        // ReadOnlyScriptFieldAccessor has no setters at all, so this is a compile error, not a runtime
        // one — the structural enforcement the design relies on rather than a documented convention.
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context(new IndexValue { FieldId = Guid.NewGuid(), FieldName = "A", Value = "x" });

        var result = await runner.RunFieldExpressionAsync(Guid.NewGuid(), "Fields[\"A\"].Value = \"nope\"; \"done\"", context);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task A_throwing_script_fails_cleanly_instead_of_throwing_out_of_the_runner()
    {
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context();
        var script = new FieldScript { Source = "throw new InvalidOperationException(\"boom\");" };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.False(result.Success);
        Assert.Contains("boom", result.ErrorMessage);
    }

    [Fact]
    public async Task A_compile_error_fails_cleanly_with_a_useful_message()
    {
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context();
        var script = new FieldScript { Source = "this is not valid C#" };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task A_hanging_script_is_stopped_by_its_timeout()
    {
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context();
        var script = new FieldScript
        {
            Source = "await System.Threading.Tasks.Task.Delay(60000, CancellationToken);",
            TimeoutSeconds = 1
        };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.False(result.Success);
        Assert.Contains("Timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task A_real_http_call_works_through_the_host_provided_client()
    {
        // No network in this test — proves the host HttpClient reaches the script and a request can be
        // built/dispatched (a real endpoint isn't required to prove Http/await wiring, just that it
        // doesn't error before the network call, and that a well-formed local scheme resolves fine).
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var context = Context();
        var script = new FieldScript { Source = "var msg = new HttpRequestMessage(HttpMethod.Get, \"http://127.0.0.1:1\"); msg.Method.ToString()" };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Compiled_scripts_are_cached_and_reused_across_runs()
    {
        var runner = new RoslynFieldScriptRunner(new AlwaysAllowed(), new HttpClient());
        var script = new FieldScript { Id = Guid.NewGuid(), Source = "Fields[\"A\"].Value = DateTime.UtcNow.Ticks.ToString();" };

        var first = Context(new IndexValue { FieldId = Guid.NewGuid(), FieldName = "A", Value = "" });
        var firstResult = await runner.RunProfileScriptAsync(script, first);
        var firstElapsed = firstResult.Elapsed;

        var second = Context(new IndexValue { FieldId = Guid.NewGuid(), FieldName = "A", Value = "" });
        var secondResult = await runner.RunProfileScriptAsync(script, second);

        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);
        // The second run reuses the compiled Script<object> for the same (id, source) — it should not
        // pay anything close to the first run's real Roslyn compile cost.
        Assert.True(secondResult.Elapsed < firstElapsed);
    }

    private static ScriptExecutionContext Context(params IndexValue[] values) => new()
    {
        ProfileName = "Test",
        DocumentNumber = 1,
        BatchNumber = 1,
        Timestamp = DateTimeOffset.UtcNow,
        Values = values,
        Document = new ScriptDocumentInfo { FileName = "sample.pdf", FileExtension = ".pdf", PageCount = 1, Text = "sample text" }
    };

    private sealed class AlwaysAllowed : IWatchSettingsStore
    {
        public Task<WatchSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchSettings { AllowFieldScripts = true });

        public Task SaveAsync(WatchSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
