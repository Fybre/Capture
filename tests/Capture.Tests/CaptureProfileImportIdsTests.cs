using Capture.App.Services;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Profiles;

namespace Capture.Tests;

public sealed class CaptureProfileImportIdsTests
{
    [Fact]
    public void Reassign_gives_every_id_a_new_value_while_preserving_internal_references()
    {
        var batchRule = new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "BATCH" };
        var batchField = new IndexField { Name = "Case number", BoundaryRuleId = batchRule.Id };

        var docRule = new SeparationStrategy { Type = SeparationStrategyType.OcrZone, TextPattern = "INVOICE" };
        var docField = new IndexField { Name = "Invoice number", BoundaryRuleId = docRule.Id };
        var script = new FieldScript { Name = "Normalize" };
        var export = new ExportDefinition
        {
            Type = ExportType.Therefore,
            FieldIds = [docField.Id, batchField.Id],
            ThereforeFieldMappings =
            [
                new ThereforeFieldMapping { FieldNo = 1, IndexFieldId = docField.Id },
                new ThereforeFieldMapping { FieldNo = 2, IndexFieldId = batchField.Id }
            ]
        };
        var type = new DocumentTypeDefinition
        {
            Name = "Invoice",
            StartRules = new RuleSet { Rules = [docRule] },
            Fields = [docField],
            Scripts = [script],
            Exports = [export]
        };
        var profile = new CaptureProfile
        {
            Batch = new BatchDefinition { StartRules = new RuleSet { Rules = [batchRule] }, Fields = [batchField] },
            DocumentTypes = [type]
        };
        profile.DefaultDocumentTypeId = type.Id;

        var originalProfileId = profile.Id;
        var originalTypeId = type.Id;
        var originalBatchRuleId = batchRule.Id;
        var originalBatchFieldId = batchField.Id;
        var originalDocRuleId = docRule.Id;
        var originalDocFieldId = docField.Id;
        var originalScriptId = script.Id;
        var originalExportId = export.Id;

        CaptureProfileImportIds.Reassign(profile);

        Assert.NotEqual(originalProfileId, profile.Id);
        Assert.NotEqual(originalTypeId, type.Id);
        Assert.NotEqual(originalBatchRuleId, batchRule.Id);
        Assert.NotEqual(originalBatchFieldId, batchField.Id);
        Assert.NotEqual(originalDocRuleId, docRule.Id);
        Assert.NotEqual(originalDocFieldId, docField.Id);
        Assert.NotEqual(originalScriptId, script.Id);
        Assert.NotEqual(originalExportId, export.Id);

        // Cross-references must follow the same remap, not just get new random ids independently.
        Assert.Equal(type.Id, profile.DefaultDocumentTypeId);
        Assert.Equal(batchRule.Id, batchField.BoundaryRuleId);
        Assert.Equal(docRule.Id, docField.BoundaryRuleId);
        Assert.Equal([docField.Id, batchField.Id], export.FieldIds);
        Assert.Equal(docField.Id, export.ThereforeFieldMappings[0].IndexFieldId);
        Assert.Equal(batchField.Id, export.ThereforeFieldMappings[1].IndexFieldId);
    }
}
