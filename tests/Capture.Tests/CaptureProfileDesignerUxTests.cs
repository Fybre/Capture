using Capture.App.ViewModels;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;

namespace Capture.Tests;

public sealed class CaptureProfileDesignerUxTests
{
    [Fact]
    public async Task Disabled_incomplete_profile_saves_as_a_draft_but_cannot_save_when_enabled()
    {
        var profile = new CaptureProfile { Enabled = false };
        var store = new RecordingProfileStore();
        var designer = new CaptureProfileDesignerViewModel(profile, store);

        Assert.True(designer.HasEnablementIssues);
        Assert.Contains("Add at least one document type.", designer.EnablementIssues);
        Assert.Contains("Choose a fallback document type.", designer.EnablementIssues);

        await designer.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, store.SaveCount);
        Assert.Contains("Disabled draft saved", designer.SaveConfirmation);
        Assert.Empty(designer.ValidationSummary);

        // Flipping Enabled on and saving while setup is incomplete must never discard the user's edits —
        // it still persists, forced back to a disabled draft, rather than silently doing nothing.
        profile.Enabled = true;
        await designer.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, store.SaveCount);
        Assert.False(profile.Enabled);
        Assert.False(designer.ProfileEnabled);
        Assert.Contains("Add at least one document type", designer.ValidationSummary);
        Assert.Contains("Choose a fallback document type", designer.ValidationSummary);
        Assert.Contains("Saved as a disabled draft", designer.SaveConfirmation);
    }

    [Fact]
    public void Duplicate_field_and_document_type_names_are_flagged()
    {
        var typeA = new DocumentTypeDefinition
        {
            Name = "Invoice",
            Fields = [new IndexField { Name = "Total" }, new IndexField { Name = "total" }]
        };
        var typeB = new DocumentTypeDefinition { Name = "Invoice" };
        var profile = new CaptureProfile
        {
            Batch = new BatchDefinition { Fields = [new IndexField { Name = "Case" }, new IndexField { Name = "Case" }] },
            DocumentTypes = [typeA, typeB]
        };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());

        Assert.Contains(designer.EnablementIssues, issue => issue.Contains("Batch field name 'Case'"));
        Assert.Contains(designer.EnablementIssues, issue => issue.Contains("Field name 'Total'") && issue.Contains("Invoice"));
        Assert.Contains(designer.EnablementIssues, issue => issue.Contains("Document type name 'Invoice'"));
    }

    [Fact]
    public void Ambiguous_recognition_rules_across_document_types_are_flagged()
    {
        var typeA = new DocumentTypeDefinition
        {
            Name = "Transcript",
            RecognitionRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.OcrZone, TextPattern = "TRANSCRIPT" }] }
        };
        var typeB = new DocumentTypeDefinition
        {
            Name = "Certificate",
            RecognitionRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.OcrZone, TextPattern = "TRANSCRIPT" }] }
        };
        var profile = new CaptureProfile { DocumentTypes = [typeA, typeB] };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());

        Assert.Contains(designer.EnablementIssues, issue => issue.Contains("Ambiguous recognition rule"));
    }

    [Fact]
    public void Enabled_export_missing_required_settings_is_flagged()
    {
        var type = new DocumentTypeDefinition
        {
            Name = "Invoice",
            Exports =
            [
                new ExportDefinition { Type = ExportType.None },
                new ExportDefinition { Type = ExportType.Csv, OutputFolder = "" },
                new ExportDefinition { Type = ExportType.Therefore, ThereforeCategoryNo = null }
            ]
        };
        var profile = new CaptureProfile { DocumentTypes = [type] };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());

        Assert.Contains(designer.EnablementIssues, issue => issue.Contains("Choose an export type"));
        Assert.Contains(designer.EnablementIssues, issue => issue.Contains("Choose an output folder"));
        Assert.Contains(designer.EnablementIssues, issue => issue.Contains("Choose a Therefore category"));
    }

    [Fact]
    public void Copying_a_document_type_twice_produces_unique_names()
    {
        var type = new DocumentTypeDefinition { Name = "Invoice" };
        var profile = new CaptureProfile { DocumentTypes = [type] };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == type);

        designer.CopyDocumentTypeCommand.Execute(null);
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == type);
        designer.CopyDocumentTypeCommand.Execute(null);

        Assert.Equal(["Invoice", "Invoice copy", "Invoice copy 2"], profile.DocumentTypes.Select(t => t.Name));
        Assert.DoesNotContain(designer.EnablementIssues, issue => issue.Contains("Document type name"));
    }

    [Fact]
    public async Task Unsaved_changes_are_tracked_and_cleared_by_save()
    {
        var profile = new CaptureProfile { Enabled = false };
        var store = new RecordingProfileStore();
        var designer = new CaptureProfileDesignerViewModel(profile, store);

        Assert.False(designer.HasUnsavedChanges());

        designer.ProfileName = "Renamed";
        Assert.True(designer.HasUnsavedChanges());
        Assert.True(designer.IsDirty);

        await designer.SaveCommand.ExecuteAsync(null);

        Assert.False(designer.HasUnsavedChanges());
        Assert.False(designer.IsDirty);
    }

    [Fact]
    public void Enablement_issue_summary_updates_as_overview_requirements_are_fixed()
    {
        var profile = new CaptureProfile { Enabled = false, Name = string.Empty };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());

        Assert.Contains("Profile name is required.", designer.EnablementIssues);
        Assert.Contains("Add at least one document type.", designer.EnablementIssues);

        designer.ProfileName = "Student records";
        designer.AddDocumentTypeCommand.Execute(null);

        Assert.False(designer.HasEnablementIssues);
        Assert.Empty(designer.EnablementIssues);
        Assert.Equal("Student records", profile.Name);
        Assert.NotNull(profile.DefaultDocumentTypeId);
    }

    [Fact]
    public void Added_document_type_can_be_renamed_and_selected_as_the_fallback_immediately()
    {
        var profile = new CaptureProfile { Enabled = false };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());

        designer.AddDocumentTypeCommand.Execute(null);
        var type = Assert.Single(designer.FallbackDocumentTypes);
        Assert.Equal(type.Id, designer.FallbackDocumentTypeId);
        designer.DocumentTypeName = "Invoice";
        designer.FallbackDocumentTypeId = type.Id;

        Assert.Equal("Invoice", type.Name);
        Assert.Equal("  Invoice", designer.SelectedNode?.Label);
        Assert.Equal(type.Id, profile.DefaultDocumentTypeId);
    }

    [Fact]
    public void Selecting_rules_and_fields_automatically_changes_the_preview_editing_context()
    {
        var type = new DocumentTypeDefinition
        {
            Name = "Invoice",
            RecognitionRules = new RuleSet
            {
                Rules = [new SeparationStrategy { Type = SeparationStrategyType.OcrZone }]
            },
            Fields = [new IndexField { Name = "Invoice number", Kind = FieldKind.Zonal }]
        };
        var profile = new CaptureProfile { DocumentTypes = [type] };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());
        Assert.Collection(designer.PageDispositionChoices,
            keep => Assert.Equal("Keep the matching page", keep.Label),
            remove => Assert.Equal("Remove the matching page", remove.Label));
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == type);

        Assert.Equal("Identify document type", designer.RecognitionRules!.DecisionTitle);
        Assert.Equal("Detect first page of a new document", designer.DocumentStartRules!.DecisionTitle);

        designer.RecognitionRules.SelectedStrategy = designer.RecognitionRules.Strategies[0];
        Assert.True(designer.IsRuleDrawingTarget);
        Assert.True(designer.CanEditSampleArea);
        Assert.Equal("Text in an area", designer.DrawingTargetLabel);

        designer.DocumentFields!.SelectedField = designer.DocumentFields.Fields[0];
        Assert.True(designer.IsFieldDrawingTarget);
        Assert.True(designer.CanEditSampleArea);
        Assert.Equal("Invoice number", designer.DrawingTargetLabel);
        Assert.Null(designer.RecognitionRules.SelectedStrategy);
    }

    [Fact]
    public async Task Redaction_editor_defaults_to_core_and_snapshots_a_selected_custom_set()
    {
        var custom = new RedactionEntitySet
        {
            Name = "Finance",
            Entities = ["CREDIT_CARD", "IBAN_CODE"]
        };
        var type = new DocumentTypeDefinition { Name = "Invoice" };
        var designer = new CaptureProfileDesignerViewModel(
            new CaptureProfile { DocumentTypes = [type] },
            new NoOpProfileStore(),
            redactionEntitySets: new RedactionSetStore(custom));

        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == type);
        await designer.InitializeAsync();

        Assert.Equal(BuiltInRedactionSets.CoreId, type.Redaction.EntitySetId);
        Assert.Equal("Core", designer.SelectedRedactionSet?.Name);
        designer.SelectedRedactionSet = designer.RedactionSets.Single(set => set.Id == custom.Id);
        Assert.Equal(custom.Id, type.Redaction.EntitySetId);
        Assert.Equal(custom.Entities, type.Redaction.Entities);
    }

    [Fact]
    public void Export_editor_opens_full_configuration_and_tracks_field_changes()
    {
        var type = new DocumentTypeDefinition
        {
            Name = "Invoice",
            Fields = [new IndexField { Name = "Invoice number" }]
        };
        var designer = new CaptureProfileDesignerViewModel(
            new CaptureProfile { DocumentTypes = [type] }, new NoOpProfileStore());
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == type);

        designer.AddExportCommand.Execute(null);

        var export = Assert.Single(designer.DocumentExports);
        Assert.True(export.IsExpanded);
        Assert.Equal("Invoice number", Assert.Single(export.FieldOptions).Field.Name);
        designer.DocumentFields!.Add(new IndexField { Name = "Total" });
        Assert.Equal(["Invoice number", "Total"], export.FieldOptions.Select(option => option.Field.Name));

        export.Type = ExportType.Csv;
        export.OutputFolder = "/exports";
        export.FileMode = ExportFileMode.Redacted;
        Assert.Equal(ExportType.Csv, export.Definition.Type);
        Assert.Equal("/exports", export.Definition.OutputFolder);
        Assert.Equal(ExportFileMode.Redacted, export.Definition.FileMode);
    }

    [Fact]
    public void Therefore_mapping_can_switch_between_an_index_field_and_a_constant()
    {
        var field = new IndexField { Name = "Invoice number" };
        var mapping = new ThereforeFieldMapping { FieldNo = 7, Caption = "Source" };
        var row = new ThereforeFieldMappingRow(mapping, [new FieldSelectionRow(new FieldRow(field), false)]);

        Assert.Equal(ThereforeMappingValueSource.IndexField, row.ValueSource);
        Assert.Equal(["Field", "Fixed"], row.ValueSourceOptions.Select(option => option.Label));
        row.SelectedField = Assert.Single(row.ProfileFields);
        Assert.Equal(field.Id, mapping.IndexFieldId);
        Assert.True(row.IsIndexFieldSource);

        row.ValueSource = ThereforeMappingValueSource.Constant;
        row.ConstantValue = "Capture";

        Assert.True(row.IsConstantSource);
        Assert.False(row.IsIndexFieldSource);
        Assert.Equal(ThereforeMappingValueSource.Constant, mapping.ValueSource);
        Assert.Equal("Capture", mapping.ConstantValue);
        Assert.Equal(field.Id, mapping.IndexFieldId);
    }

    [Fact]
    public void Export_editor_exposes_batch_and_document_fields_with_their_scope()
    {
        var batchField = new FieldRow(new IndexField { Name = "Case number" });
        var documentField = new FieldRow(new IndexField { Name = "Case number" });
        var mapping = new ThereforeFieldMapping
        {
            FieldNo = 7,
            Caption = "Case No",
            IndexFieldId = batchField.Id
        };
        var definition = new ExportDefinition { ThereforeFieldMappings = [mapping] };

        var row = new ExportDefinitionRow(definition, [documentField], [batchField]);

        Assert.Equal(["Batch", "Document"], row.FieldOptions.Select(option => option.ScopeLabel));
        Assert.Equal(batchField.Id, Assert.Single(row.ThereforeMappings).SelectedField?.Id);
        Assert.True(Assert.Single(row.FieldOptions, option => option.IsBatchField).IsSelected);
    }

    [Fact]
    public void Copy_document_type_regenerates_nested_ids_and_preserves_internal_references()
    {
        var rule = new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "Invoice" };
        var field = new IndexField { Name = "Invoice number", BoundaryRuleId = rule.Id };
        var script = new FieldScript { Name = "Normalize" };
        var export = new ExportDefinition
        {
            Type = ExportType.Csv,
            FieldIds = [field.Id],
            ThereforeFieldMappings = [new ThereforeFieldMapping { FieldNo = 1, IndexFieldId = field.Id }]
        };
        var original = new DocumentTypeDefinition
        {
            Name = "Invoice",
            StartRules = new RuleSet { Rules = [rule] },
            Fields = [field],
            Scripts = [script],
            Exports = [export]
        };
        var profile = new CaptureProfile { DocumentTypes = [original] };
        var designer = new CaptureProfileDesignerViewModel(profile, new NoOpProfileStore());
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == original);

        designer.CopyDocumentTypeCommand.Execute(null);

        var copy = Assert.Single(profile.DocumentTypes, type => type != original);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.NotEqual(rule.Id, copy.StartRules.Rules[0].Id);
        Assert.NotEqual(field.Id, copy.Fields[0].Id);
        Assert.NotEqual(script.Id, copy.Scripts[0].Id);
        Assert.NotEqual(export.Id, copy.Exports[0].Id);
        Assert.Equal(copy.StartRules.Rules[0].Id, copy.Fields[0].BoundaryRuleId);
        Assert.Equal(copy.Fields[0].Id, Assert.Single(copy.Exports[0].FieldIds));
        Assert.Equal(copy.Fields[0].Id, copy.Exports[0].ThereforeFieldMappings[0].IndexFieldId);
    }

    [Fact]
    public void Clear_area_removes_the_selected_field_zone_and_updates_its_ui_state()
    {
        var field = new IndexField
        {
            Name = "Invoice number",
            Kind = FieldKind.Zonal,
            Zone = new ZoneRect { PageNumber = 1, X = .1f, Y = .2f, Width = .3f, Height = .1f }
        };
        var type = new DocumentTypeDefinition { Name = "Invoice", Fields = [field] };
        var designer = new CaptureProfileDesignerViewModel(
            new CaptureProfile { DocumentTypes = [type] }, new NoOpProfileStore());
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == type);
        designer.DocumentFields!.SelectedField = designer.DocumentFields.Fields[0];

        Assert.True(designer.CanClearSampleArea);
        Assert.Equal("Clear field zone", designer.ClearAreaLabel);
        designer.ClearSampleZoneCommand.Execute(null);

        Assert.Null(field.Zone);
        Assert.False(designer.CanClearSampleArea);
        Assert.Equal("Field zone cleared.", designer.SampleStatus);
    }

    [Fact]
    public async Task Document_field_script_test_receives_the_designers_batch_fields()
    {
        var batchField = new IndexField { Name = "Category Type", Kind = FieldKind.Text };
        var documentField = new IndexField
        {
            Name = "Category",
            Kind = FieldKind.Text,
            PostProcessScript = "Context.Batch.Fields[\"Category Type\"].Value"
        };
        var type = new DocumentTypeDefinition { Name = "Record", Fields = [documentField] };
        var runner = new RecordingScriptRunner();
        var designer = new CaptureProfileDesignerViewModel(
            new CaptureProfile
            {
                Batch = new BatchDefinition { Fields = [batchField] },
                DocumentTypes = [type]
            },
            new NoOpProfileStore(),
            scriptRunner: runner);
        designer.BatchFields.Fields[0].LiveValue = "Student";
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == type);

        await designer.DocumentFields!.TestPostProcessScriptCommand.ExecuteAsync(designer.DocumentFields.Fields[0]);

        var value = Assert.Single(runner.LastContext!.BatchValues!);
        Assert.Equal(batchField.Id, value.FieldId);
        Assert.Equal("Student", value.Value);
    }

    private sealed class NoOpProfileStore : ICaptureProfileStore
    {
        public Task<IReadOnlyList<CaptureProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureProfile>>([]);
        public Task<CaptureProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CaptureProfile?>(null);
        public Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingProfileStore : ICaptureProfileStore
    {
        public int SaveCount { get; private set; }
        public Task<IReadOnlyList<CaptureProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureProfile>>([]);
        public Task<CaptureProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureProfile?>(null);
        public Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingScriptRunner : IFieldScriptRunner
    {
        public bool IsAvailable => true;
        public ScriptExecutionContext? LastContext { get; private set; }

        public Task<ScriptRunResult> RunProfileScriptAsync(
            FieldScript script,
            ScriptExecutionContext context,
            CancellationToken cancellationToken = default,
            string sharedSource = "")
        {
            LastContext = context;
            return Task.FromResult(ScriptRunResult.Ok(null, TimeSpan.Zero));
        }

        public Task<ScriptRunResult> RunFieldExpressionAsync(
            Guid scriptCacheKey,
            string expression,
            ScriptExecutionContext context,
            CancellationToken cancellationToken = default,
            string sharedSource = "")
        {
            LastContext = context;
            return Task.FromResult(ScriptRunResult.Ok("Student", TimeSpan.Zero));
        }
    }

    private sealed class RedactionSetStore(params RedactionEntitySet[] sets) : IRedactionEntitySetStore
    {
        public Task<IReadOnlyList<RedactionEntitySet>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RedactionEntitySet>>(sets);
        public Task<RedactionEntitySet?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(sets.FirstOrDefault(set => set.Id == id));
        public Task SaveAsync(RedactionEntitySet set, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
