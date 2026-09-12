using Capture.App.ViewModels;
using Capture.App.Views;
using Capture.App.Converters;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using System.Globalization;

namespace Capture.Tests;

public class SharedDesignerComponentTests
{
    [Fact]
    public void Field_editor_view_loads_its_runtime_xaml_bindings()
    {
        var exception = Record.Exception(() => new FieldCollectionEditorView());

        Assert.Null(exception);
    }

    [Fact]
    public void Zonal_fields_offer_plain_language_page_scopes()
    {
        var row = new FieldRow(new IndexField { Name = "Invoice number", Kind = FieldKind.Zonal });

        Assert.True(row.HasPageScope);
        Assert.Collection(row.PageScopes,
            first => Assert.Equal("First page", first.Label),
            number => Assert.Equal("Specific page", number.Label),
            any => Assert.Equal("Any page", any.Label));
    }

    [Fact]
    public void Barcode_field_explicitly_switches_between_entire_page_and_a_drawn_area()
    {
        var originalZone = new ZoneRect { PageNumber = 3, X = .1f, Y = .1f, Width = .5f, Height = .5f };
        var field = new IndexField { Name = "Barcode", Kind = FieldKind.Barcode, Zone = originalZone };
        var row = new FieldRow(field);

        Assert.Equal(BarcodeScanArea.SelectedArea, row.BarcodeScanArea);
        Assert.Contains("page 3", row.BarcodeAreaStatus);

        row.BarcodeScanArea = BarcodeScanArea.EntirePage;
        Assert.Null(field.Zone);
        Assert.Contains("entire", row.BarcodeAreaStatus);

        row.BarcodeScanArea = BarcodeScanArea.SelectedArea;
        Assert.Contains("Draw", row.BarcodeAreaStatus);
        row.SetZone(originalZone);
        Assert.Equal(originalZone, field.Zone);
        Assert.Equal(BarcodeScanArea.SelectedArea, row.BarcodeScanArea);
    }

    [Fact]
    public void Field_editor_reorders_and_roundtrips_existing_models()
    {
        var first = new IndexField { Name = "First" };
        var second = new IndexField { Name = "Second" };
        var editor = new FieldCollectionEditorViewModel([first, second]) { SelectedField = new FieldRow(second) };
        editor.SelectedField = editor.Fields[1];
        editor.ReorderField(second.Id, first.Id);
        Assert.Equal([second.Id, first.Id], editor.ToModels().Select(field => field.Id));
    }

    [Fact]
    public void Field_value_source_reopens_with_its_saved_rule_and_can_switch_to_another_field()
    {
        var rule = new SeparationStrategyRow(new Capture.Core.Import.SeparationStrategy
        {
            Type = Capture.Core.Import.SeparationStrategyType.Barcode,
            Name = "Student header"
        });
        var source = new IndexField { Name = "Student number", Kind = FieldKind.Barcode };
        var target = new IndexField { Name = "Function", Kind = FieldKind.Text, BoundaryRuleId = rule.Id };
        var editor = new FieldCollectionEditorViewModel(
            [source, target],
            boundaryRules: [rule],
            boundaryRuleLabel: "First-page rule");

        editor.SelectedField = editor.Fields.Single(field => field.Id == target.Id);

        Assert.Equal(FieldValueSourceKind.BoundaryRule, editor.SelectedValueSource?.Kind);
        Assert.Equal(rule.Id, editor.SelectedValueSource?.BoundaryRuleId);

        editor.SelectedField = null;
        editor.SelectedField = editor.Fields.Single(field => field.Id == target.Id);
        Assert.Equal(rule.Id, editor.SelectedValueSource?.BoundaryRuleId);

        editor.SelectedValueSource = editor.ValueSourceOptions.Single(option => option.Key == $"field:{source.Id:N}");
        Assert.Null(target.BoundaryRuleId);
        Assert.Equal("{Student number}", target.DefaultValueTemplate);
        Assert.Contains(editor.ValueSourceOptions, option => option.Template == "{Page#}");
    }

    [Fact]
    public void Batch_separator_value_chooses_one_batch_start_rule_and_offers_no_ignored_sources()
    {
        var rule = new SeparationStrategyRow(new Capture.Core.Import.SeparationStrategy
        {
            Type = Capture.Core.Import.SeparationStrategyType.Barcode,
            Name = "Student header"
        });
        var field = new IndexField { Name = "Student number", Kind = FieldKind.BatchSeparatorValue };
        var editor = new FieldCollectionEditorViewModel(
            [field], boundaryRules: [rule], boundaryRuleLabel: "Batch-start rule", isBatchScope: true);

        editor.SelectedField = Assert.Single(editor.Fields);

        Assert.True(editor.SelectedField.CanPopulateValue);
        Assert.True(editor.SelectedField.AllowsPostProcessScript);
        Assert.Equal([FieldValueSourceKind.None, FieldValueSourceKind.BoundaryRule],
            editor.ValueSourceOptions.Select(option => option.Kind));
        Assert.Equal("Choose batch-start rule…", editor.ValueSourceOptions[0].Label);
        Assert.DoesNotContain(editor.ValueSourceOptions,
            option => option.Label == "Normal extraction or manual entry");

        editor.SelectedValueSource = editor.ValueSourceOptions.Single(option => option.BoundaryRuleId == rule.Id);
        Assert.Equal(rule.Id, field.BoundaryRuleId);
    }

    [Fact]
    public void Batch_separator_value_is_only_available_for_batch_fields()
    {
        var batchEditor = new FieldCollectionEditorViewModel(isBatchScope: true);
        var documentEditor = new FieldCollectionEditorViewModel();

        Assert.Contains(FieldKind.BatchSeparatorValue, batchEditor.FieldKinds);
        Assert.DoesNotContain(FieldKind.BatchSeparatorValue, documentEditor.FieldKinds);
    }

    [Fact]
    public void Sensitive_batch_value_is_masked_by_default_and_can_be_explicitly_revealed()
    {
        var value = new IndexValue { FieldName = "Student number", Value = "X00007", Sensitive = true };
        var row = new IndexValueRow(value, 80, null, isBatch: true);

        Assert.True(row.IsSensitiveBatch);
        Assert.True(row.IsSensitiveBatchMasked);
        Assert.Equal('●', row.PasswordChar);
        Assert.Equal("Show", row.SensitiveValueToggleLabel);

        row.RevealSensitiveValue = true;

        Assert.False(row.IsSensitiveBatchMasked);
        Assert.Equal("Hide", row.SensitiveValueToggleLabel);
        Assert.Equal("X00007", row.Text);
    }

    [Fact]
    public void Read_only_field_does_not_offer_copy_to_selection()
    {
        var editable = new IndexValueRow(new IndexValue { IsReadOnly = false }, 80, null);
        var readOnly = new IndexValueRow(new IndexValue { IsReadOnly = true }, 80, null);
        var batch = new IndexValueRow(new IndexValue { IsReadOnly = false }, 80, null, isBatch: true);

        Assert.True(editable.CanCopyToSelection);
        Assert.False(readOnly.CanCopyToSelection);
        Assert.False(batch.CanCopyToSelection);
    }

    [Fact]
    public void Presidio_and_manual_redactions_can_be_adjusted_but_sensitive_field_bounds_stay_linked()
    {
        Assert.True(new RedactionCandidateRow(new RedactionCandidate { Source = RedactionSource.Presidio }).CanAdjustBounds);
        Assert.True(new RedactionCandidateRow(new RedactionCandidate { Source = RedactionSource.Manual }).CanAdjustBounds);
        Assert.False(new RedactionCandidateRow(new RedactionCandidate { Source = RedactionSource.SensitiveField }).CanAdjustBounds);
    }

    [Theory]
    [InlineData(RedactionStatus.None, false, false, false)]
    [InlineData(RedactionStatus.PendingReview, true, false, false)]
    [InlineData(RedactionStatus.Applied, false, true, false)]
    [InlineData(RedactionStatus.Failed, false, false, true)]
    public void Table_redaction_indicator_exposes_each_redaction_state(
        RedactionStatus status,
        bool pending,
        bool applied,
        bool failed)
    {
        var row = new DocumentRow(new CaptureDocument
        {
            OriginalFileName = "sample.pdf",
            StoredPath = "/tmp/sample.pdf",
            RedactionStatus = status,
            RedactionError = status == RedactionStatus.Failed ? "Sidecar unavailable" : null
        });

        Assert.Equal(pending, row.IsRedactionPending);
        Assert.Equal(applied, row.IsRedactionApplied);
        Assert.Equal(failed, row.IsRedactionFailed);
        if (failed)
            Assert.Contains("Sidecar unavailable", row.RedactionStatusTooltip);
    }

    [Fact]
    public void Table_masks_sensitive_batch_value_and_uses_the_correct_scope_when_names_match()
    {
        var document = new DocumentRow(new CaptureDocument
        {
            OriginalFileName = "sample.pdf",
            StoredPath = "/tmp/sample.pdf"
        });
        document.SetBatchIndexes(
        [
            new IndexValue { FieldName = "Reference", Value = "batch-secret", Sensitive = true }
        ]);
        document.SetDocumentIndexes(
        [
            new IndexValue { FieldName = "Reference", Value = "document-value" }
        ]);
        var converter = IndexCellTextConverter.Instance;

        Assert.Equal("••••••", converter.Convert(
            document, typeof(string), new IndexCellBinding("Reference", true), CultureInfo.InvariantCulture));
        Assert.Equal("document-value", converter.Convert(
            document, typeof(string), new IndexCellBinding("Reference", false), CultureInfo.InvariantCulture));
        Assert.DoesNotContain("batch-secret", document.IndexesSummary);
    }

    [Fact]
    public void Editing_an_index_notifies_dynamic_table_cells_to_refresh()
    {
        var value = new IndexValue { FieldName = "Separator", Value = "original" };
        var document = new DocumentRow(new CaptureDocument
        {
            OriginalFileName = "sample.pdf",
            StoredPath = "/tmp/sample.pdf"
        });
        document.SetBatchIndexes([value]);
        var notifications = new List<string?>();
        document.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        value.Value = "changed";
        document.NotifyIndexes();

        Assert.Contains(nameof(DocumentRow.IndexCellSource), notifications);
        Assert.Equal("changed", IndexCellTextConverter.Instance.Convert(
            document.IndexCellSource,
            typeof(string),
            new IndexCellBinding("Separator", true),
            CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Document_type_indicator_is_compact_and_updates_with_the_assignment()
    {
        var document = new DocumentRow(new CaptureDocument
        {
            OriginalFileName = "sample.pdf",
            StoredPath = "/tmp/sample.pdf"
        });
        var notifications = new List<string?>();
        document.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        Assert.Equal("Unclassified", document.DocumentTypeDisplay);
        Assert.Equal("?", document.DocumentTypeMarker);

        document.ProfileName = "Student record";

        Assert.Equal("Student record", document.DocumentTypeDisplay);
        Assert.Equal("T", document.DocumentTypeMarker);
        Assert.Equal("Document type: Student record", document.DocumentTypeTooltip);
        Assert.Contains(nameof(DocumentRow.DocumentTypeDisplay), notifications);
        Assert.Contains(nameof(DocumentRow.DocumentTypeMarker), notifications);
        Assert.Contains(nameof(DocumentRow.DocumentTypeTooltip), notifications);
    }

    [Fact]
    public void Split_reindex_retains_inherited_header_values_only_when_no_new_value_was_found()
    {
        var inheritedOnlyId = Guid.NewGuid();
        var newlyExtractedId = Guid.NewGuid();
        var manualId = Guid.NewGuid();
        var inherited = new List<IndexValue>
        {
            new() { FieldId = inheritedOnlyId, Value = "HEADER-001", Confidence = 96, PageNumber = 1 },
            new() { FieldId = newlyExtractedId, Value = "OLD", Confidence = 90, PageNumber = 1 },
            new() { FieldId = manualId, Value = "CORRECTED", Confidence = 100, IsManual = true, PageNumber = 1 }
        };
        var refreshed = new List<IndexValue>
        {
            new() { FieldId = inheritedOnlyId, Value = string.Empty },
            new() { FieldId = newlyExtractedId, Value = "NEW", Confidence = 88, PageNumber = 2 },
            new() { FieldId = manualId, Value = "EXTRACTED", Confidence = 75, PageNumber = 2 }
        };

        MainViewModel.PreserveInheritedSplitValues(refreshed, inherited);

        Assert.Equal("HEADER-001", refreshed[0].Value);
        Assert.Equal("NEW", refreshed[1].Value);
        Assert.Equal("CORRECTED", refreshed[2].Value);
        Assert.True(refreshed[2].IsManual);
    }

    [Fact]
    public void Script_editor_preserves_shared_source_and_trigger()
    {
        var script = new FieldScript { Name = "Existing", Trigger = ScriptTrigger.BeforeExport };
        var editor = new ScriptCollectionEditorViewModel([script], "int Helper() => 1;");
        editor.AddCommand.Execute(null);
        Assert.Equal("int Helper() => 1;", editor.SharedSource);
        Assert.Equal(ScriptTrigger.BeforeExport, editor.ToModels()[0].Trigger);
        Assert.Equal(ScriptTrigger.AfterFieldsPopulated, editor.ToModels()[1].Trigger);
    }

    [Fact]
    public async Task Script_editor_defines_source_inline_and_through_popout_and_can_test_it()
    {
        var testedSource = string.Empty;
        var editor = new ScriptCollectionEditorViewModel(
            editSource: _ => Task.FromResult<string?>("Fields[\"Total\"].Value = \"42\";"),
            testSource: (row, _) =>
            {
                testedSource = row.Source;
                return Task.FromResult("Test passed");
            });

        editor.AddCommand.Execute(null);
        var row = Assert.Single(editor.Scripts);
        await editor.PopOutCommand.ExecuteAsync(row);
        await editor.TestCommand.ExecuteAsync(row);

        Assert.Equal("Fields[\"Total\"].Value = \"42\";", row.Source);
        Assert.Equal(row.Source, testedSource);
        Assert.Equal("Test passed", row.TestResult);
    }

    [Fact]
    public async Task Field_editor_popout_writes_script_text_back_to_the_selected_field()
    {
        var field = new IndexField { Name = "Calculated", Kind = FieldKind.Script };
        var editor = new FieldCollectionEditorViewModel(
            [field],
            (_, title, _) => Task.FromResult<string?>($"// {title}"),
            (_, kind) => Task.FromResult($"Tested {kind}"));
        var row = Assert.Single(editor.Fields);

        await editor.PopOutFieldScriptCommand.ExecuteAsync(row);
        await editor.TestFieldScriptCommand.ExecuteAsync(row);

        Assert.Equal("// Script expression — Calculated", row.ScriptExpression);
        Assert.Equal(row.ScriptExpression, field.ScriptExpression);
        Assert.Equal("Tested Expression", row.ScriptTestResult);
    }

    [Fact]
    public void Script_editors_open_contextual_scripting_help()
    {
        var openCount = 0;
        var scripts = new ScriptCollectionEditorViewModel(openScriptingHelp: () => openCount++);
        var fields = new FieldCollectionEditorViewModel(openScriptingHelp: () => openCount++);

        scripts.OpenScriptingHelpCommand.Execute(null);
        fields.OpenScriptingHelpCommand.Execute(null);

        Assert.Equal(2, openCount);
    }

    [Fact]
    public async Task Field_editor_routes_sample_actions_without_reaching_through_its_parent_view()
    {
        var keyRequested = false;
        var valueRequested = false;
        var aiRequested = false;
        var editor = new FieldCollectionEditorViewModel(
            suggestKeyFromSample: () => keyRequested = true,
            suggestValueFromSample: () => valueRequested = true,
            extractAiSample: () => { aiRequested = true; return Task.CompletedTask; });

        editor.SelectedField = editor.Add(new IndexField { Kind = FieldKind.KeyValue });
        editor.SuggestKeyFromSampleCommand.Execute(null);
        editor.SuggestValueFromSampleCommand.Execute(null);
        editor.SelectedField = editor.Add(new IndexField { Kind = FieldKind.Ai });
        await editor.ExtractAiSampleCommand.ExecuteAsync(null);

        Assert.True(keyRequested);
        Assert.True(valueRequested);
        Assert.True(aiRequested);
    }
}
