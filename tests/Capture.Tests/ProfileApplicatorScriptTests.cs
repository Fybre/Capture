using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Scripting;

namespace Capture.Tests;

/// <summary>Covers ProfileApplicator's wiring of IFieldScriptRunner (profile-level FieldScripts and
/// per-field Script-kind ScriptExpressions) via a fake runner — no real Roslyn compilation here, so
/// this stays in the fast unit-test tier. Real compile-and-run coverage lives in
/// Capture.Scripting.Tests.</summary>
public class ProfileApplicatorScriptTests
{
    [Fact]
    public async Task Profile_script_mutates_a_non_manual_field()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Total", Kind = FieldKind.Text }],
            Scripts = [new FieldScript { Name = "Set total", Source = "Fields[\"Total\"].Value = \"42\";" }]
        };
        var runner = new FakeScriptRunner
        {
            OnProfileScript = (_, ctx) =>
            {
                ctx.Values.Single(v => v.FieldName == "Total").Value = "42";
                return ScriptRunResult.Ok(null, TimeSpan.Zero);
            }
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal("42", values.Single().Value);
    }

    [Fact]
    public async Task Both_profile_scripts_and_field_expressions_receive_the_profile_s_shared_source()
    {
        var fieldId = Guid.NewGuid();
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField { Name = "Total", Kind = FieldKind.Text },
                new IndexField { Id = fieldId, Name = "Computed", Kind = FieldKind.Script, ScriptExpression = "irrelevant" }
            ],
            Scripts = [new FieldScript { Source = "irrelevant" }],
            SharedScriptSource = "string Helper() => \"shared\";"
        };
        var runner = new FakeScriptRunner();

        await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.All(runner.ReceivedSharedSources, source => Assert.Equal(profile.SharedScriptSource, source));
        Assert.Equal(2, runner.ReceivedSharedSources.Count); // one profile script + one field expression
    }

    [Fact]
    public async Task Scripts_see_document_level_facts_from_the_lattices_and_document_passed_to_ApplyAsync()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words = [new LatticeWord { Text = "Hello", Confidence = 95, X = 0.1f, Y = 0.1f, Width = 0.1f, Height = 0.04f }]
        };
        var document = new CaptureDocument { OriginalFileName = "invoice.PDF", StoredPath = "/tmp/invoice.pdf", PageCount = 1 };
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Info", Kind = FieldKind.Text }],
            Scripts = [new FieldScript { Source = "..." }]
        };
        ScriptDocumentInfo? seen = null;
        var runner = new FakeScriptRunner
        {
            OnProfileScript = (_, ctx) =>
            {
                seen = ctx.Document;
                return ScriptRunResult.Ok(null, TimeSpan.Zero);
            }
        };

        await new ProfileApplicator(scripts: runner).ApplyAsync(profile, [lattice], document: document);

        Assert.NotNull(seen);
        Assert.Equal("invoice.PDF", seen!.FileName);
        Assert.Equal(".pdf", seen.FileExtension); // lowercased regardless of the original casing
        Assert.Equal(1, seen.PageCount);
        Assert.Contains("Hello", seen.Text);
    }

    [Fact]
    public async Task Profile_script_write_to_a_manually_edited_field_is_reverted_afterward()
    {
        var fieldId = Guid.NewGuid();
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Id = fieldId, Name = "Notes", Kind = FieldKind.Text }],
            Scripts = [new FieldScript { Source = "irrelevant" }]
        };
        var runner = new FakeScriptRunner
        {
            OnProfileScript = (_, ctx) =>
            {
                ctx.Values.Single(v => v.FieldId == fieldId).Value = "SCRIPT OVERWRITE";
                return ScriptRunResult.Ok(null, TimeSpan.Zero);
            }
        };
        var existing = new[]
        {
            new IndexValue { FieldId = fieldId, FieldName = "Notes", Value = "user typed this", IsManual = true }
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, [], existingValues: existing);

        var notes = values.Single();
        Assert.Equal("user typed this", notes.Value);
        Assert.True(notes.IsManual);
    }

    [Fact]
    public async Task Failed_profile_script_leaves_results_unchanged_and_does_not_throw()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Total", Kind = FieldKind.Text }],
            Scripts = [new FieldScript { Source = "boom" }]
        };
        var runner = new FakeScriptRunner
        {
            OnProfileScript = (_, _) => ScriptRunResult.Failed("compile error", TimeSpan.Zero)
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal(string.Empty, values.Single().Value);
    }

    [Fact]
    public async Task No_runner_configured_is_a_clean_no_op()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Total", Kind = FieldKind.Text }],
            Scripts = [new FieldScript { Source = "Fields[\"Total\"].Value = \"should never run\";" }]
        };

        var values = await new ProfileApplicator().ApplyAsync(profile, []);

        Assert.Equal(string.Empty, values.Single().Value);
    }

    [Fact]
    public async Task Scripts_are_skipped_when_the_runner_reports_unavailable()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Total", Kind = FieldKind.Text }],
            Scripts = [new FieldScript { Source = "..." }]
        };
        var runner = new FakeScriptRunner
        {
            IsAvailable = false,
            OnProfileScript = (_, _) => throw new InvalidOperationException("should not run when unavailable")
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal(string.Empty, values.Single().Value);
    }

    [Fact]
    public async Task Multiple_enabled_scripts_run_in_list_order_and_see_earlier_mutations()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Log", Kind = FieldKind.Text }],
            Scripts =
            [
                new FieldScript { Name = "First", Source = "..." },
                new FieldScript { Name = "Second", Source = "..." }
            ]
        };
        var runner = new FakeScriptRunner
        {
            OnProfileScript = (script, ctx) =>
            {
                var field = ctx.Values.Single(v => v.FieldName == "Log");
                field.Value += script.Name;
                return ScriptRunResult.Ok(null, TimeSpan.Zero);
            }
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal("FirstSecond", values.Single().Value);
    }

    [Fact]
    public async Task Disabled_and_wrong_trigger_scripts_are_skipped()
    {
        var calls = new List<string>();
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Log", Kind = FieldKind.Text }],
            Scripts =
            [
                new FieldScript { Name = "Disabled", Enabled = false, Source = "..." },
                new FieldScript { Name = "WrongTrigger", Trigger = ScriptTrigger.BeforeExport, Source = "..." },
                new FieldScript { Name = "Runs", Trigger = ScriptTrigger.AfterFieldsPopulated, Source = "..." }
            ]
        };
        var runner = new FakeScriptRunner
        {
            OnProfileScript = (script, _) =>
            {
                calls.Add(script.Name);
                return ScriptRunResult.Ok(null, TimeSpan.Zero);
            }
        };

        await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal(["Runs"], calls);
    }

    [Fact]
    public async Task Script_field_expressions_resolve_in_field_order_and_can_chain()
    {
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField { Name = "A", Kind = FieldKind.Script, ScriptExpression = "expr-a" },
                new IndexField { Name = "B", Kind = FieldKind.Script, ScriptExpression = "expr-b" }
            ]
        };
        var runner = new FakeScriptRunner
        {
            OnFieldExpression = (_, expr, ctx) => expr == "expr-a"
                ? ScriptRunResult.Ok("A-VALUE", TimeSpan.Zero)
                : ScriptRunResult.Ok($"B-sees-{ctx.Values.Single(v => v.FieldName == "A").Value}", TimeSpan.Zero)
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal("A-VALUE", values.Single(v => v.FieldName == "A").Value);
        Assert.Equal("B-sees-A-VALUE", values.Single(v => v.FieldName == "B").Value);
    }

    [Fact]
    public async Task Manual_edit_on_a_script_field_is_preserved_across_reapply()
    {
        var fieldId = Guid.NewGuid();
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Id = fieldId, Name = "Computed", Kind = FieldKind.Script, ScriptExpression = "expr" }]
        };
        var runner = new FakeScriptRunner
        {
            OnFieldExpression = (_, _, _) => ScriptRunResult.Ok("FROM SCRIPT", TimeSpan.Zero)
        };
        var existing = new[]
        {
            new IndexValue { FieldId = fieldId, FieldName = "Computed", Value = "user override", IsManual = true }
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, [], existingValues: existing);

        Assert.Equal("user override", values.Single().Value);
    }

    [Fact]
    public async Task Button_kind_fields_are_never_auto_extracted()
    {
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Name = "Lookup", Kind = FieldKind.Button, ButtonScriptSource = "Fields[\"Lookup\"].Value = \"x\";" }]
        };

        // No runner at all — a Button field's own value only ever changes via an explicit button
        // click (RunButtonFieldAsync/RunButtonFieldTestAsync), never automatically during ApplyAsync.
        var values = await new ProfileApplicator().ApplyAsync(profile, []);

        Assert.Equal(string.Empty, values.Single().Value);
    }

    [Fact]
    public async Task Manual_edit_on_a_button_field_is_preserved_across_reapply()
    {
        var fieldId = Guid.NewGuid();
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Id = fieldId, Name = "Lookup", Kind = FieldKind.Button, ButtonScriptSource = "..." }]
        };
        var existing = new[]
        {
            new IndexValue { FieldId = fieldId, FieldName = "Lookup", Value = "Customer ABC found", IsManual = true }
        };

        var values = await new ProfileApplicator().ApplyAsync(profile, [], existingValues: existing);

        Assert.Equal("Customer ABC found", values.Single().Value);
    }

    [Fact]
    public async Task Post_process_script_rewrites_a_field_s_own_extracted_value()
    {
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField
                {
                    Name = "Customer",
                    Kind = FieldKind.Zonal,
                    PostProcessScript = "Fields[\"Customer\"].Value.Replace(\" . \", \" \")"
                }
            ]
        };
        // Real extraction leaves this field blank (no lattices supplied) — what matters here is that
        // ApplyPostProcessScriptsAsync calls RunFieldExpressionAsync with this field's PostProcessScript
        // and writes whatever the runner returns back into the field's own value.
        var runner = new FakeScriptRunner
        {
            OnFieldExpression = (_, _, _) => ScriptRunResult.Ok("Sumisho Coal Australia Holdings Pty Ltd", TimeSpan.Zero)
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal("Sumisho Coal Australia Holdings Pty Ltd", values.Single().Value);
    }

    [Fact]
    public async Task Post_process_script_runs_after_a_text_field_s_default_template_so_it_sees_the_resolved_value()
    {
        var fieldId = Guid.NewGuid();
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField
                {
                    Id = fieldId,
                    Name = "Greeting",
                    Kind = FieldKind.Text,
                    DefaultValueTemplate = "hello",
                    PostProcessScript = "irrelevant"
                }
            ]
        };
        string? seenPreCleanupValue = null;
        var runner = new FakeScriptRunner
        {
            OnFieldExpression = (_, _, ctx) =>
            {
                seenPreCleanupValue = ctx.Values.Single(v => v.FieldId == fieldId).Value;
                return ScriptRunResult.Ok("HELLO", TimeSpan.Zero);
            }
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);

        Assert.Equal("hello", seenPreCleanupValue); // the template's resolved value, not blank
        Assert.Equal("HELLO", values.Single().Value);
    }

    [Fact]
    public async Task Post_process_script_is_skipped_on_a_manually_edited_field()
    {
        var fieldId = Guid.NewGuid();
        var profile = new IndexingProfile
        {
            Fields = [new IndexField { Id = fieldId, Name = "Notes", Kind = FieldKind.Text, PostProcessScript = "irrelevant" }]
        };
        var runner = new FakeScriptRunner
        {
            OnFieldExpression = (_, _, _) => throw new InvalidOperationException("should not run on a manual value")
        };
        var existing = new[]
        {
            new IndexValue { FieldId = fieldId, FieldName = "Notes", Value = "user typed this", IsManual = true }
        };

        var values = await new ProfileApplicator(scripts: runner).ApplyAsync(profile, [], existingValues: existing);

        Assert.Equal("user typed this", values.Single().Value);
    }

    [Theory]
    [InlineData(FieldKind.Script)]
    [InlineData(FieldKind.Button)]
    [InlineData(FieldKind.BatchSeparatorValue)]
    public async Task Post_process_script_never_runs_for_kinds_with_their_own_script_semantics(FieldKind kind)
    {
        var profile = new IndexingProfile
        {
            Fields =
            [
                new IndexField
                {
                    Name = "Field",
                    Kind = kind,
                    ScriptExpression = kind == FieldKind.Script ? "\"x\"" : null,
                    ButtonScriptSource = kind == FieldKind.Button ? "..." : "",
                    PostProcessScript = "irrelevant"
                }
            ]
        };
        var runner = new FakeScriptRunner
        {
            OnFieldExpression = (_, expression, _) => expression == "irrelevant"
                ? throw new InvalidOperationException("PostProcessScript must not run for this kind")
                : ScriptRunResult.Ok("x", TimeSpan.Zero)
        };

        // Should not throw — confirms ApplyPostProcessScriptsAsync's Kind filter excludes this field.
        await new ProfileApplicator(scripts: runner).ApplyAsync(profile, []);
    }

    private sealed class FakeScriptRunner : IFieldScriptRunner
    {
        public bool IsAvailable { get; set; } = true;

        public Func<FieldScript, ScriptExecutionContext, ScriptRunResult>? OnProfileScript { get; set; }

        public Func<Guid, string, ScriptExecutionContext, ScriptRunResult>? OnFieldExpression { get; set; }

        public List<string> ReceivedSharedSources { get; } = [];

        public Task<ScriptRunResult> RunProfileScriptAsync(FieldScript script, ScriptExecutionContext context, CancellationToken cancellationToken = default, string sharedSource = "")
        {
            ReceivedSharedSources.Add(sharedSource);
            return Task.FromResult(OnProfileScript?.Invoke(script, context) ?? ScriptRunResult.Ok(null, TimeSpan.Zero));
        }

        public Task<ScriptRunResult> RunFieldExpressionAsync(Guid scriptCacheKey, string expression, ScriptExecutionContext context, CancellationToken cancellationToken = default, string sharedSource = "")
        {
            ReceivedSharedSources.Add(sharedSource);
            return Task.FromResult(OnFieldExpression?.Invoke(scriptCacheKey, expression, context) ?? ScriptRunResult.Ok(null, TimeSpan.Zero));
        }
    }
}
